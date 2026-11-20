using System.Diagnostics;
using System.Globalization;
using System.Text;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Migration;
using Argus.Core.Contracts.System;
using Argus.Core.Journal;
using Microsoft.Extensions.Logging;

namespace Argus.Migration.Engine;

/// <summary>
/// Unified compiled C# CopyOS migration engine supporting Internal (SATA/NVMe/RAID) and External (USB) profiles,
/// automatic BLOCK (NtfsVssCloner) vs VSS+DISM mode selection, strict safety confirmation, live progress,
/// and guaranteed resource cleanup in finally.
/// </summary>
public sealed class WindowsMigrationEngine : IMigrationEngine
{
    private readonly IMigrationHostOperations _host;
    private readonly IPrivilegeService _privilegeService;
    private readonly IOperationJournal _journal;
    private readonly ILogger<WindowsMigrationEngine> _logger;
    private readonly string _logsDirectory;

    public WindowsMigrationEngine(
        IMigrationHostOperations host,
        IPrivilegeService privilegeService,
        IOperationJournal journal,
        ILogger<WindowsMigrationEngine> logger,
        string? logsDirectory = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logsDirectory = string.IsNullOrWhiteSpace(logsDirectory)
            ? ArgusPaths.GetDefaultLogsDirectory()
            : logsDirectory;
    }

    public Task<SourceOsInfo> InspectLiveOsAsync(CancellationToken cancellationToken = default)
        => _host.InspectLiveOsAsync(cancellationToken);

    public async Task<IReadOnlyList<MigrationTargetDisk>> DiscoverTargetDisksAsync(
        MigrationProfile profile,
        CancellationToken cancellationToken = default)
    {
        var source = await _host.InspectLiveOsAsync(cancellationToken).ConfigureAwait(false);
        return await _host.DiscoverTargetDisksAsync(
            profile,
            source.SourceDiskNumber,
            source.SystemDriveLetter,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<MigrationPreflightResult> EvaluatePlanAsync(
        MigrationPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var source = await _host.InspectLiveOsAsync(cancellationToken).ConfigureAwait(false);
        var targets = await _host.DiscoverTargetDisksAsync(
            request.Profile,
            source.SourceDiskNumber,
            source.SystemDriveLetter,
            cancellationToken).ConfigureAwait(false);

        var target = targets.FirstOrDefault(d => d.DiskNumber == request.TargetDiskNumber);
        var localVolumes = await _host.GetLocalScratchVolumesAsync(cancellationToken).ConfigureAwait(false);

        return MigrationPlanEvaluator.Evaluate(
            source,
            target,
            localVolumes,
            request,
            _privilegeService.IsAdministrator);
    }

    public async Task<MigrationResult> ExecuteAsync(
        MigrationPlanRequest request,
        string explicitConfirmationToken,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sw = Stopwatch.StartNew();

        // 1. Strict Administrator Gate
        if (!_privilegeService.IsAdministrator)
        {
            const string msg = "Для клонування ОС потрібні права адміністратора (запустіть кореневий Argus.exe або натисніть щит UAC).";
            await _journal.RecordAsync(
                "argus.migration",
                OperationSeverity.Warning,
                "MigrationBlockedNotAdmin",
                $"Спробу міграції на Диск {request.TargetDiskNumber} заблоковано без прав адміністратора",
                succeeded: false,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return new MigrationResult(
                Succeeded: false,
                WasCancelled: false,
                Profile: request.Profile,
                UsedCloneMode: request.RequestedCloneMode,
                PartitionStyle: request.PartitionStyle,
                TargetDiskNumber: request.TargetDiskNumber,
                Elapsed: TimeSpan.Zero,
                LogFilePath: null,
                Summary: msg);
        }

        // 2. Strict Human Confirmation Token Gate (WIPE-DISK-<N>)
        if (!MigrationConfirmationPolicy.IsValidConfirmation(request.TargetDiskNumber, explicitConfirmationToken))
        {
            string expected = MigrationConfirmationPolicy.BuildExpectedToken(request.TargetDiskNumber);
            string msg = $"Невірний код підтвердження. Для очищення Диска {request.TargetDiskNumber} потрібно ввести точний код {expected}.";

            await _journal.RecordAsync(
                "argus.migration",
                OperationSeverity.Warning,
                "MigrationBlockedConfirmation",
                $"Міграцію на Диск {request.TargetDiskNumber} заблоковано: невірний код підтвердження",
                succeeded: false,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return new MigrationResult(
                Succeeded: false,
                WasCancelled: false,
                Profile: request.Profile,
                UsedCloneMode: request.RequestedCloneMode,
                PartitionStyle: request.PartitionStyle,
                TargetDiskNumber: request.TargetDiskNumber,
                Elapsed: TimeSpan.Zero,
                LogFilePath: null,
                Summary: msg);
        }

        Report(progress, MigrationPhase.InspectingSource, 3, 0, 0, 0, null, "Перевірка активної ОС та цільового диска…");
        var source = await _host.InspectLiveOsAsync(cancellationToken).ConfigureAwait(false);
        var targets = await _host.DiscoverTargetDisksAsync(
            request.Profile,
            source.SourceDiskNumber,
            source.SystemDriveLetter,
            cancellationToken).ConfigureAwait(false);
        var target = targets.FirstOrDefault(d => d.DiskNumber == request.TargetDiskNumber);
        var localVolumes = await _host.GetLocalScratchVolumesAsync(cancellationToken).ConfigureAwait(false);

        Report(progress, MigrationPhase.ValidatingTarget, 6, 0, source.UsedBytes, 0, null, "Перевірка плану міграції та обсягу диска…");
        var preflight = MigrationPlanEvaluator.Evaluate(
            source,
            target,
            localVolumes,
            request,
            _privilegeService.IsAdministrator);

        if (!preflight.CanProceed || target is null)
        {
            string reason = preflight.BlockingErrors.FirstOrDefault() ?? "План міграції не пройшов перевірку безпеки.";
            await _journal.RecordAsync(
                "argus.migration",
                OperationSeverity.Warning,
                "MigrationBlockedPreflight",
                reason,
                succeeded: false,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return new MigrationResult(
                Succeeded: false,
                WasCancelled: false,
                Profile: request.Profile,
                UsedCloneMode: preflight.ResolvedCloneMode,
                PartitionStyle: request.PartitionStyle,
                TargetDiskNumber: request.TargetDiskNumber,
                Elapsed: sw.Elapsed,
                LogFilePath: null,
                Summary: reason);
        }

        // Prepare dedicated CopyOS log file
        Directory.CreateDirectory(_logsDirectory);
        string logFilePath = Path.Combine(
            _logsDirectory,
            $"copyos-migration-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        object logGate = new();

        void WriteLog(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            _logger.LogInformation("{CopyOsLog}", line);
            lock (logGate)
            {
                try
                {
                    File.AppendAllText(logFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // best effort file logging
                }
            }
        }

        var cleanupState = new MigrationCleanupState();
        CloneMode resolvedMode = preflight.ResolvedCloneMode;

        WriteLog($"=== ARGUS CopyOS Migration Started ({request.Profile}) ===");
        WriteLog(string.Format(
            CultureInfo.InvariantCulture,
            "Source: {0}: (Disk {1}, {2}, {3:N2} GiB used / {4:N2} GiB) -> Target: Disk {5} ({6}, {7}, {8:N2} GiB) | Mode={9} | Layout={10}",
            source.SystemDriveLetter,
            source.SourceDiskNumber,
            source.SourceBusType,
            source.UsedBytes / (1024.0 * 1024.0 * 1024.0),
            source.PartitionSizeBytes / (1024.0 * 1024.0 * 1024.0),
            target.DiskNumber,
            target.FriendlyName,
            target.BusType,
            target.SizeBytes / (1024.0 * 1024.0 * 1024.0),
            resolvedMode,
            request.PartitionStyle));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (bootLetter, osLetter) = _host.AllocateFreeDriveLetters();
            WriteLog($"Виділено тимчасові літери: Boot={bootLetter}:, OS={osLetter}:");

            // Phase 3: VSS Snapshot
            Report(progress, MigrationPhase.CreatingVssSnapshot, 10, 0, source.UsedBytes, 0, null, $"Створення VSS-знімка для {source.SystemDriveLetter}:\\…");
            var snapshot = await _host.CreateVssSnapshotAsync(source.SystemDriveLetter, WriteLog, cancellationToken)
                .ConfigureAwait(false);
            cleanupState.ShadowId = snapshot.ShadowId;

            // Phase 4: Partition target
            Report(progress, MigrationPhase.PartitioningTarget, 16, 0, source.UsedBytes, 0, null, $"Розмітка Диска {target.DiskNumber} ({request.PartitionStyle})…");
            var targetInfo = await _host.PartitionAndPrepareTargetAsync(
                request,
                source,
                target,
                resolvedMode,
                bootLetter,
                osLetter,
                WriteLog,
                cancellationToken).ConfigureAwait(false);

            // Phase 5: Clone Data (BLOCK or VSS+DISM)
            if (resolvedMode == CloneMode.BlockVssBitmap)
            {
                Report(progress, MigrationPhase.CloningData, 20, 0, source.UsedBytes, 0, null, "Запуск двобуферного блокового клонування NtfsVssCloner…");
                await _host.ExecuteBlockCloneAsync(source, targetInfo, snapshot, WriteLog, progress, cancellationToken)
                    .ConfigureAwait(false);

                // Release VSS snapshot early once raw copy is done
                await _host.DeleteVssSnapshotAsync(snapshot.ShadowId, WriteLog).ConfigureAwait(false);
                cleanupState.ShadowId = null;

                // Phase 6: Mount, chkdsk /f /x, extend to full disk capacity
                Report(progress, MigrationPhase.CheckingAndExtendingVolume, 78, source.UsedBytes, source.UsedBytes, 0, null, $"Перевірка chkdsk /f /x та розширення тому {osLetter}:…");
                targetInfo = await _host.MountCheckAndExtendBlockVolumeAsync(request.Profile, targetInfo, WriteLog, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                char scratchLetter = preflight.ScratchDriveLetter
                    ?? throw new InvalidOperationException("Не визначено буферний том для DISM.");

                Report(progress, MigrationPhase.CloningData, 20, 0, source.UsedBytes, 0, null, $"Адаптивне клонування VSS + DISM (буфер {scratchLetter}:)…");
                await _host.ExecuteDismCaptureAndApplyAsync(
                    request.Profile,
                    source,
                    targetInfo,
                    snapshot,
                    scratchLetter,
                    cleanupState,
                    WriteLog,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                await _host.CleanupResourcesAsync(cleanupState, WriteLog).ConfigureAwait(false);
            }

            // Phase 7: Redirected User Folders (USB auto-fix / Internal opt-in Desktop copy)
            Report(progress, MigrationPhase.FixingUserFolders, 84, source.UsedBytes, source.UsedBytes, 0, null, "Перевірка перенесених тек профілю користувача…");
            await _host.FixRedirectedUserFoldersAsync(request, source, targetInfo.OsDriveLetter, cleanupState, WriteLog, cancellationToken)
                .ConfigureAwait(false);

            // Phase 8: Offline SYSTEM Registry (MountedDevices + BootDriverFlags + SATA/NVMe or USB drivers)
            Report(progress, MigrationPhase.UpdatingOfflineRegistry, 90, source.UsedBytes, source.UsedBytes, 0, null, "Оновлення офлайн-реєстру SYSTEM і драйверів завантаження…");
            await _host.UpdateOfflineSystemRegistryAsync(request, source, targetInfo, cleanupState, WriteLog, cancellationToken)
                .ConfigureAwait(false);

            // Phase 9: Bootloader (bcdboot / bcdedit / bootsect / ESP GUID)
            Report(progress, MigrationPhase.InstallingBootloader, 95, source.UsedBytes, source.UsedBytes, 0, null, "Встановлення завантажувача Windows…");
            await _host.InstallBootloaderAsync(request, source, targetInfo, WriteLog, cancellationToken)
                .ConfigureAwait(false);

            Report(progress, MigrationPhase.CleaningUp, 98, source.UsedBytes, source.UsedBytes, 0, null, "Очищення тимчасових ресурсів (VSS / реєстр)…");
            await _host.CleanupResourcesAsync(cleanupState, WriteLog).ConfigureAwait(false);

            sw.Stop();
            WriteLog($"=== ARGUS CopyOS Migration Completed in {sw.Elapsed:hh\\:mm\\:ss} ===");

            try
            {
                string targetLogCopy = $"{targetInfo.OsDriveLetter}:\\ArgusMigration.log";
                if (Directory.Exists($"{targetInfo.OsDriveLetter}:\\"))
                {
                    File.Copy(logFilePath, targetLogCopy, overwrite: true);
                }
            }
            catch
            {
                // Non-fatal copy
            }

            Report(progress, MigrationPhase.Completed, 100, source.UsedBytes, source.UsedBytes, 0, TimeSpan.Zero, $"Клонування на Диск {target.DiskNumber} завершено.");

            string summary = $"Клонування ОС на Диск {target.DiskNumber} ({target.FriendlyName}) успішно завершено за {sw.Elapsed:hh\\:mm\\:ss}.";
            await _journal.RecordAsync(
                "argus.migration",
                OperationSeverity.Info,
                "MigrationCompleted",
                $"{request.Profile} | {resolvedMode} | {request.PartitionStyle} | Диск {target.DiskNumber}",
                succeeded: true,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return new MigrationResult(
                Succeeded: true,
                WasCancelled: false,
                Profile: request.Profile,
                UsedCloneMode: resolvedMode,
                PartitionStyle: request.PartitionStyle,
                TargetDiskNumber: target.DiskNumber,
                Elapsed: sw.Elapsed,
                LogFilePath: logFilePath,
                Summary: summary);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            WriteLog("Операцію клонування скасовано користувачем. Виконується очищення ресурсів...");
            await _host.CleanupResourcesAsync(cleanupState, WriteLog).ConfigureAwait(false);
            Report(progress, MigrationPhase.Cancelled, 0, 0, source.UsedBytes, 0, null, "Міграцію скасовано. Тимчасові VSS-знімки та реєстр очищено.");

            await _journal.RecordAsync(
                "argus.migration",
                OperationSeverity.Warning,
                "MigrationCancelled",
                $"Клонування на Диск {target.DiskNumber} скасовано користувачем",
                succeeded: false,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return new MigrationResult(
                Succeeded: false,
                WasCancelled: true,
                Profile: request.Profile,
                UsedCloneMode: resolvedMode,
                PartitionStyle: request.PartitionStyle,
                TargetDiskNumber: target.DiskNumber,
                Elapsed: sw.Elapsed,
                LogFilePath: logFilePath,
                Summary: $"Клонування на Диск {target.DiskNumber} скасовано. Тимчасові ресурси очищено.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            WriteLog($"FATAL ERROR: {ex}");
            await _host.CleanupResourcesAsync(cleanupState, WriteLog).ConfigureAwait(false);
            Report(progress, MigrationPhase.Failed, 0, 0, source.UsedBytes, 0, null, $"Помилка міграції: {ex.Message}");

            await _journal.RecordAsync(
                "argus.migration",
                OperationSeverity.Error,
                "MigrationFailed",
                $"Диск {target.DiskNumber}: {ex.Message}",
                succeeded: false,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return new MigrationResult(
                Succeeded: false,
                WasCancelled: false,
                Profile: request.Profile,
                UsedCloneMode: resolvedMode,
                PartitionStyle: request.PartitionStyle,
                TargetDiskNumber: target.DiskNumber,
                Elapsed: sw.Elapsed,
                LogFilePath: logFilePath,
                Summary: $"Помилка клонування на Диск {target.DiskNumber}: {ex.Message}",
                ErrorDetails: ex.ToString());
        }
        finally
        {
            // Guaranteed idempotent safety net (does not overwrite terminal UI status)
            await _host.CleanupResourcesAsync(cleanupState, WriteLog).ConfigureAwait(false);
        }
    }

    private static void Report(
        IProgress<MigrationProgress>? progress,
        MigrationPhase phase,
        int pct,
        long copied,
        long total,
        double speed,
        TimeSpan? eta,
        string status)
    {
        progress?.Report(new MigrationProgress(phase, pct, copied, total, speed, eta, status));
    }
}
