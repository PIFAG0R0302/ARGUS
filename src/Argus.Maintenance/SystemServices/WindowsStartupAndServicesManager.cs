using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Argus.Core.Contracts.System;
using Argus.Core.Journal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Argus.Maintenance.SystemServices;

/// <summary>
/// Manages Windows startup points (Registry Run keys, Startup folders via native StartupApproved,
/// and Task Scheduler via Schedule.Service COM) as well as Windows Services (advapi32.dll) with
/// hard protection for critical system services and UAC-on-demand checks.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsStartupAndServicesManager : IStartupAndServicesManager
{
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string StartupApprovedRun32SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string StartupApprovedFolderSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    private const int ErrorAccessDenied = 5;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceWin32 = 0x00000030;
    private const uint ServiceStateAll = 0x00000003;
    private const uint ScEnumProcessInfo = 0;

    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceAcceptStop = 0x00000001;

    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceAutoStart = 2;
    private const uint ServiceDemandStart = 3;
    private const uint ServiceDisabled = 4;
    private const uint ServiceConfigDescription = 1;

    private const uint ServiceStopped = 1;
    private const uint ServiceStartPending = 2;
    private const uint ServiceStopPending = 3;
    private const uint ServiceRunning = 4;
    private const uint ServiceContinuePending = 5;
    private const uint ServicePausePending = 6;
    private const uint ServicePaused = 7;

    private readonly IPrivilegeService _privilegeService;
    private readonly IOperationJournal _journal;
    private readonly ILogger<WindowsStartupAndServicesManager> _logger;

    public WindowsStartupAndServicesManager(
        IPrivilegeService privilegeService,
        IOperationJournal journal,
        ILogger<WindowsStartupAndServicesManager> logger)
    {
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IReadOnlyList<StartupEntryItem>> GetStartupEntriesAsync(
        bool includeSystemTasks = false,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CollectStartupEntries(includeSystemTasks, cancellationToken), cancellationToken);
    }

    public async Task<SystemActionResult> SetStartupEntryEnabledAsync(
        StartupEntryItem entry,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!OperatingSystem.IsWindows())
        {
            return new SystemActionResult(false, RequiresElevation: false, "Керування автозапуском доступне лише у Windows.");
        }

        if (entry.RequiresElevation && !_privilegeService.IsAdministrator)
        {
            string uacMsg = $"Потрібні права адміністратора (UAC), щоб змінити стан системного елемента «{entry.Name}».";
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "StartupToggleRequiresElevation",
                $"{entry.Source} | {entry.Name} -> {(enabled ? "Enable" : "Disable")}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: true, uacMsg);
        }

        SystemActionResult result = await Task.Run(
            () => ToggleStartupEntryCore(entry, enabled),
            cancellationToken).ConfigureAwait(false);

        await _journal.RecordAsync(
            "argus.maintenance",
            result.Succeeded ? OperationSeverity.Success : OperationSeverity.Warning,
            "StartupToggle",
            $"{entry.Source} | {entry.Name} -> {(enabled ? "Увімкнено" : "Вимкнено")}: {result.Message}",
            succeeded: result.Succeeded,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }

    public Task<IReadOnlyList<ManagedServiceItem>> GetManagedServicesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CollectManagedServices(cancellationToken), cancellationToken);
    }

    public async Task<SystemActionResult> ControlServiceAsync(
        string serviceName,
        ServiceControlAction action,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new SystemActionResult(false, RequiresElevation: false, "Не вказано назву служби Windows.");
        }

        string trimmed = serviceName.Trim();
        if (action is ServiceControlAction.Stop or ServiceControlAction.Restart &&
            CriticalServiceProtection.IsCriticalService(trimmed))
        {
            string blockedMsg = $"Служба «{trimmed}» є критичною для роботи Windows і захищена від зупинки або перезапуску.";
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "CriticalServiceBlocked",
                $"Служба={trimmed}, Дія={action}: {blockedMsg}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: false, blockedMsg);
        }

        if (!_privilegeService.IsAdministrator)
        {
            string uacMsg = $"Потрібні права адміністратора (UAC) для керування службою «{trimmed}».";
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "ServiceControlRequiresElevation",
                $"Служба={trimmed}, Дія={action}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: true, uacMsg);
        }

        var result = await Task.Run(
            () => ExecuteServiceControl(trimmed, action, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        await _journal.RecordAsync(
            "argus.maintenance",
            result.Succeeded ? OperationSeverity.Success : OperationSeverity.Warning,
            "ServiceControl",
            $"Служба={trimmed}, Дія={action}: {result.Message}",
            succeeded: result.Succeeded,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<SystemActionResult> SetServiceStartModeAsync(
        string serviceName,
        ServiceStartModeOption startMode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new SystemActionResult(false, RequiresElevation: false, "Не вказано назву служби Windows.");
        }

        string trimmed = serviceName.Trim();
        if (startMode == ServiceStartModeOption.Disabled &&
            CriticalServiceProtection.IsCriticalService(trimmed))
        {
            string blockedMsg = $"Служба «{trimmed}» є критичною для Windows — переведення у стан «Вимкнено» заблоковано.";
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "CriticalServiceDisableBlocked",
                $"Служба={trimmed}: {blockedMsg}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: false, blockedMsg);
        }

        if (!_privilegeService.IsAdministrator)
        {
            string uacMsg = $"Потрібні права адміністратора (UAC) для зміни типу запуску служби «{trimmed}».";
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "ServiceStartModeRequiresElevation",
                $"Служба={trimmed}, Режим={startMode}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: true, uacMsg);
        }

        var result = await Task.Run(
            () => ExecuteChangeServiceStartMode(trimmed, startMode),
            cancellationToken).ConfigureAwait(false);

        await _journal.RecordAsync(
            "argus.maintenance",
            result.Succeeded ? OperationSeverity.Success : OperationSeverity.Warning,
            "ServiceStartMode",
            $"Служба={trimmed}, Режим={FormatStartModeOption(startMode)}: {result.Message}",
            succeeded: result.Succeeded,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }

    internal static bool ParseStartupApprovedEnabled(byte[]? raw)
    {
        if (raw is null || raw.Length == 0)
        {
            return true;
        }

        // Windows Task Manager StartupApproved format:
        // First byte 0x02 or 0x06 (even) => Enabled; 0x03 or odd => Disabled.
        return (raw[0] & 0x01) == 0;
    }

    internal static byte[] CreateStartupApprovedBytes(bool enabled, DateTimeOffset? timestampUtc = null)
    {
        byte[] data = new byte[12];
        if (enabled)
        {
            data[0] = 0x02;
            return data;
        }

        data[0] = 0x03;
        long fileTime = (timestampUtc ?? DateTimeOffset.UtcNow).ToFileTime();
        byte[] ftBytes = BitConverter.GetBytes(fileTime);
        Array.Copy(ftBytes, 0, data, 4, Math.Min(8, ftBytes.Length));
        return data;
    }

    private IReadOnlyList<StartupEntryItem> CollectStartupEntries(bool includeSystemTasks, CancellationToken ct)
    {
        var items = new List<StartupEntryItem>(64);

        CollectRegistryRunEntries(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            RunSubKey,
            StartupApprovedRunSubKey,
            StartupSourceKind.RegistryCurrentUserRun,
            @"HKCU\...\Run",
            requiresElevation: false,
            items,
            ct);

        CollectRegistryRunEntries(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            RunSubKey,
            StartupApprovedRunSubKey,
            StartupSourceKind.RegistryLocalMachineRun,
            @"HKLM\...\Run",
            requiresElevation: true,
            items,
            ct);

        CollectRegistryRunEntries(
            RegistryHive.LocalMachine,
            RegistryView.Registry32,
            RunSubKey,
            StartupApprovedRun32SubKey,
            StartupSourceKind.RegistryLocalMachineWow6432Run,
            @"HKLM\...\WOW6432Node\...\Run",
            requiresElevation: true,
            items,
            ct);

        CollectStartupFolderEntries(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            RegistryHive.CurrentUser,
            StartupSourceKind.StartupFolderUser,
            requiresElevation: false,
            items,
            ct);

        CollectStartupFolderEntries(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            RegistryHive.LocalMachine,
            StartupSourceKind.StartupFolderCommon,
            requiresElevation: true,
            items,
            ct);

        CollectTaskSchedulerEntries(includeSystemTasks, items, ct);

        items.Sort(static (a, b) =>
        {
            int byEnabled = b.IsEnabled.CompareTo(a.IsEnabled);
            if (byEnabled != 0)
            {
                return byEnabled;
            }

            int bySource = a.Source.CompareTo(b.Source);
            return bySource != 0 ? bySource : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        return items;
    }

    private void CollectRegistryRunEntries(
        RegistryHive hive,
        RegistryView view,
        string runSubKey,
        string approvedSubKey,
        StartupSourceKind source,
        string locationDisplay,
        bool requiresElevation,
        List<StartupEntryItem> target,
        CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var runKey = baseKey.OpenSubKey(runSubKey, writable: false);
            if (runKey is null)
            {
                return;
            }

            // StartupApproved for Run32 lives in the 64-bit HKLM Explorer view on x64 Windows.
            using var approvedBase = RegistryKey.OpenBaseKey(hive, hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);
            using var approvedKey = approvedBase.OpenSubKey(approvedSubKey, writable: false);

            foreach (string valueName in runKey.GetValueNames())
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(valueName))
                {
                    continue;
                }

                string command = Convert.ToString(runKey.GetValue(valueName), CultureInfo.InvariantCulture) ?? string.Empty;
                byte[]? approvedRaw = approvedKey?.GetValue(valueName) as byte[];
                bool isEnabled = ParseStartupApprovedEnabled(approvedRaw);

                target.Add(new StartupEntryItem(
                    Id: $"{source}:{valueName}",
                    Name: valueName,
                    Command: command.Trim(),
                    Source: source,
                    LocationPath: locationDisplay,
                    IsEnabled: isEnabled,
                    RequiresElevation: requiresElevation,
                    IsSystemOrMicrosoft: false,
                    PublisherOrAuthor: hive == RegistryHive.CurrentUser ? "Поточний користувач" : "Усі користувачі (HKLM)"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Unable to inspect startup registry key {Hive}\\{SubKey}.", hive, runSubKey);
        }
    }

    private void CollectStartupFolderEntries(
        string folderPath,
        RegistryHive approvedHive,
        StartupSourceKind source,
        bool requiresElevation,
        List<StartupEntryItem> target,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        try
        {
            using var approvedBase = RegistryKey.OpenBaseKey(
                approvedHive,
                approvedHive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);
            using var approvedKey = approvedBase.OpenSubKey(StartupApprovedFolderSubKey, writable: false);

            foreach (string filePath in Directory.EnumerateFiles(folderPath))
            {
                ct.ThrowIfCancellationRequested();
                string fileName = Path.GetFileName(filePath);
                if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte[]? approvedRaw = approvedKey?.GetValue(fileName) as byte[];
                bool isEnabled = ParseStartupApprovedEnabled(approvedRaw);

                target.Add(new StartupEntryItem(
                    Id: $"{source}:{fileName}",
                    Name: Path.GetFileNameWithoutExtension(fileName),
                    Command: filePath,
                    Source: source,
                    LocationPath: folderPath,
                    IsEnabled: isEnabled,
                    RequiresElevation: requiresElevation,
                    IsSystemOrMicrosoft: false,
                    PublisherOrAuthor: requiresElevation ? "Спільна тека Startup" : "Тека Startup користувача"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Unable to inspect startup folder '{Folder}'.", folderPath);
        }
    }

    private void CollectTaskSchedulerEntries(
        bool includeSystemTasks,
        List<StartupEntryItem> target,
        CancellationToken ct)
    {
        object? scheduler = null;
        try
        {
            Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null)
            {
                return;
            }

            scheduler = Activator.CreateInstance(schedulerType);
            if (scheduler is null)
            {
                return;
            }

            ComCall(scheduler, "Connect");
            object? rootFolder = ComCall(scheduler, "GetFolder", @"\");
            if (rootFolder is null)
            {
                return;
            }

            try
            {
                EnumerateTaskFolder(rootFolder, includeSystemTasks, target, ct);
            }
            finally
            {
                ReleaseCom(rootFolder);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Unable to enumerate Windows Task Scheduler entries.");
        }
        finally
        {
            ReleaseCom(scheduler);
        }
    }

    private void EnumerateTaskFolder(
        object folder,
        bool includeSystemTasks,
        List<StartupEntryItem> target,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string folderPath = Convert.ToString(ComGet(folder, "Path"), CultureInfo.InvariantCulture) ?? @"\";
        bool isMicrosoftFolder = folderPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase);

        if (!isMicrosoftFolder || includeSystemTasks)
        {
            object? tasks = null;
            try
            {
                tasks = ComCall(folder, "GetTasks", 1); // TASK_ENUM_HIDDEN = 1
                int count = Convert.ToInt32(ComGet(tasks, "Count"), CultureInfo.InvariantCulture);
                for (int i = 1; i <= count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    object? task = null;
                    try
                    {
                        task = ComGet(tasks, "Item", i);
                        if (task is null)
                        {
                            continue;
                        }

                        string name = Convert.ToString(ComGet(task, "Name"), CultureInfo.InvariantCulture) ?? string.Empty;
                        string path = Convert.ToString(ComGet(task, "Path"), CultureInfo.InvariantCulture) ?? name;
                        bool enabled = Convert.ToBoolean(ComGet(task, "Enabled"), CultureInfo.InvariantCulture);

                        (string command, string author, bool hasLogonOrBootTrigger) = ExtractTaskSummary(task);
                        if (!includeSystemTasks && !hasLogonOrBootTrigger && isMicrosoftFolder)
                        {
                            continue;
                        }

                        target.Add(new StartupEntryItem(
                            Id: $"TaskScheduler:{path}",
                            Name: name,
                            Command: string.IsNullOrWhiteSpace(command) ? path : command,
                            Source: StartupSourceKind.TaskScheduler,
                            LocationPath: path,
                            IsEnabled: enabled,
                            RequiresElevation: true,
                            IsSystemOrMicrosoft: isMicrosoftFolder,
                            PublisherOrAuthor: string.IsNullOrWhiteSpace(author) ? "Планувальник завдань" : author));
                    }
                    catch
                    {
                        // Skip individual inaccessible task
                    }
                    finally
                    {
                        ReleaseCom(task);
                    }
                }
            }
            catch
            {
                // Ignore folder read failures
            }
            finally
            {
                ReleaseCom(tasks);
            }
        }

        object? subFolders = null;
        try
        {
            subFolders = ComCall(folder, "GetFolders", 0);
            int subCount = Convert.ToInt32(ComGet(subFolders, "Count"), CultureInfo.InvariantCulture);
            for (int i = 1; i <= subCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                object? sub = null;
                try
                {
                    sub = ComGet(subFolders, "Item", i);
                    if (sub is not null)
                    {
                        string subPath = Convert.ToString(ComGet(sub, "Path"), CultureInfo.InvariantCulture) ?? string.Empty;
                        if (!includeSystemTasks && subPath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        EnumerateTaskFolder(sub, includeSystemTasks, target, ct);
                    }
                }
                catch
                {
                    // Skip inaccessible subfolder
                }
                finally
                {
                    ReleaseCom(sub);
                }
            }
        }
        catch
        {
            // Ignore subfolder enumeration errors
        }
        finally
        {
            ReleaseCom(subFolders);
        }
    }

    private static (string Command, string Author, bool HasStartupTrigger) ExtractTaskSummary(object task)
    {
        object? definition = null;
        object? regInfo = null;
        object? actions = null;
        object? triggers = null;
        try
        {
            definition = ComGet(task, "Definition");
            if (definition is null)
            {
                return (string.Empty, string.Empty, false);
            }

            string author = string.Empty;
            try
            {
                regInfo = ComGet(definition, "RegistrationInfo");
                author = Convert.ToString(ComGet(regInfo, "Author"), CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch
            {
                // Optional author metadata
            }

            string command = string.Empty;
            try
            {
                actions = ComGet(definition, "Actions");
                int actionCount = Convert.ToInt32(ComGet(actions, "Count"), CultureInfo.InvariantCulture);
                if (actionCount > 0)
                {
                    object? firstAction = ComGet(actions, "Item", 1);
                    try
                    {
                        int actionType = Convert.ToInt32(ComGet(firstAction, "Type"), CultureInfo.InvariantCulture);
                        if (actionType == 0) // TASK_ACTION_EXEC
                        {
                            string execPath = Convert.ToString(ComGet(firstAction, "Path"), CultureInfo.InvariantCulture) ?? string.Empty;
                            string args = Convert.ToString(ComGet(firstAction, "Arguments"), CultureInfo.InvariantCulture) ?? string.Empty;
                            command = string.IsNullOrWhiteSpace(args) ? execPath : $"{execPath} {args}";
                        }
                    }
                    finally
                    {
                        ReleaseCom(firstAction);
                    }
                }
            }
            catch
            {
                // Optional exec action
            }

            bool hasStartupTrigger = false;
            try
            {
                triggers = ComGet(definition, "Triggers");
                int triggerCount = Convert.ToInt32(ComGet(triggers, "Count"), CultureInfo.InvariantCulture);
                for (int i = 1; i <= triggerCount; i++)
                {
                    object? trig = ComGet(triggers, "Item", i);
                    try
                    {
                        int triggerType = Convert.ToInt32(ComGet(trig, "Type"), CultureInfo.InvariantCulture);
                        // 8 = Boot, 9 = Logon
                        if (triggerType is 8 or 9)
                        {
                            hasStartupTrigger = true;
                            break;
                        }
                    }
                    finally
                    {
                        ReleaseCom(trig);
                    }
                }
            }
            catch
            {
                // Optional trigger check
            }

            return (command.Trim(), author.Trim(), hasStartupTrigger);
        }
        finally
        {
            ReleaseCom(triggers);
            ReleaseCom(actions);
            ReleaseCom(regInfo);
            ReleaseCom(definition);
        }
    }

    private SystemActionResult ToggleStartupEntryCore(StartupEntryItem entry, bool enabled)
    {
        try
        {
            return entry.Source switch
            {
                StartupSourceKind.RegistryCurrentUserRun =>
                    WriteStartupApprovedValue(RegistryHive.CurrentUser, StartupApprovedRunSubKey, entry.Name, enabled),
                StartupSourceKind.RegistryLocalMachineRun =>
                    WriteStartupApprovedValue(RegistryHive.LocalMachine, StartupApprovedRunSubKey, entry.Name, enabled),
                StartupSourceKind.RegistryLocalMachineWow6432Run =>
                    WriteStartupApprovedValue(RegistryHive.LocalMachine, StartupApprovedRun32SubKey, entry.Name, enabled),
                StartupSourceKind.StartupFolderUser =>
                    WriteStartupApprovedValue(RegistryHive.CurrentUser, StartupApprovedFolderSubKey, Path.GetFileName(entry.Command), enabled),
                StartupSourceKind.StartupFolderCommon =>
                    WriteStartupApprovedValue(RegistryHive.LocalMachine, StartupApprovedFolderSubKey, Path.GetFileName(entry.Command), enabled),
                StartupSourceKind.TaskScheduler =>
                    ToggleScheduledTask(entry.LocationPath, enabled),
                _ => new SystemActionResult(false, RequiresElevation: false, "Невідоме джерело автозапуску.")
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для зміни «{entry.Name}».");
        }
        catch (SecurityException)
        {
            return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для зміни «{entry.Name}».");
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для зміни завдання «{entry.Name}».");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle startup entry '{Name}' ({Source}).", entry.Name, entry.Source);
            return new SystemActionResult(false, RequiresElevation: false, $"Не вдалося змінити «{entry.Name}»: {ex.Message}");
        }
    }

    private static SystemActionResult WriteStartupApprovedValue(
        RegistryHive hive,
        string approvedSubKey,
        string valueName,
        bool enabled)
    {
        if (string.IsNullOrWhiteSpace(valueName))
        {
            return new SystemActionResult(false, RequiresElevation: false, "Порожнє ім'я параметра автозапуску.");
        }

        using var baseKey = RegistryKey.OpenBaseKey(
            hive,
            hive == RegistryHive.LocalMachine ? RegistryView.Registry64 : RegistryView.Default);
        using var approvedKey = baseKey.CreateSubKey(approvedSubKey, writable: true);
        if (approvedKey is null)
        {
            return new SystemActionResult(false, RequiresElevation: hive == RegistryHive.LocalMachine, "Не вдалося відкрити розділ StartupApproved у реєстрі.");
        }

        byte[] bytes = CreateStartupApprovedBytes(enabled);
        approvedKey.SetValue(valueName, bytes, RegistryValueKind.Binary);

        string stateText = enabled ? "увімкнено" : "вимкнено";
        return new SystemActionResult(true, RequiresElevation: false, $"Автозапуск «{valueName}» {stateText} (StartupApproved).");
    }

    private static SystemActionResult ToggleScheduledTask(string taskPath, bool enabled)
    {
        object? scheduler = null;
        object? rootFolder = null;
        object? task = null;
        try
        {
            Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, "Служба планувальника завдань Windows недоступна.");
            }

            scheduler = Activator.CreateInstance(schedulerType);
            if (scheduler is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, "Не вдалося ініціалізувати Schedule.Service.");
            }

            ComCall(scheduler, "Connect");
            rootFolder = ComCall(scheduler, "GetFolder", @"\");
            if (rootFolder is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, "Не вдалося відкрити корінь планувальника завдань.");
            }

            task = ComCall(rootFolder, "GetTask", taskPath);
            if (task is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, $"Завдання «{taskPath}» не знайдено.");
            }

            ComSet(task, "Enabled", enabled);
            string stateText = enabled ? "увімкнено" : "вимкнено";
            return new SystemActionResult(true, RequiresElevation: false, $"Завдання планувальника «{taskPath}» {stateText}.");
        }
        finally
        {
            ReleaseCom(task);
            ReleaseCom(rootFolder);
            ReleaseCom(scheduler);
        }
    }

    private IReadOnlyList<ManagedServiceItem> CollectManagedServices(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        IntPtr scm = OpenSCManagerW(null, null, ScManagerEnumerateService);
        if (scm == IntPtr.Zero)
        {
            _logger.LogWarning("OpenSCManagerW failed with Win32 error {Error}.", Marshal.GetLastWin32Error());
            return [];
        }

        var items = new List<ManagedServiceItem>(256);
        IntPtr enumBuffer = IntPtr.Zero;
        IntPtr configBuffer = IntPtr.Zero;
        IntPtr descBuffer = IntPtr.Zero;
        uint configBufferSize = 8192;
        uint descBufferSize = 4096;

        try
        {
            uint bytesNeeded = 0;
            uint servicesReturned = 0;
            uint resumeHandle = 0;

            _ = EnumServicesStatusExW(
                scm,
                ScEnumProcessInfo,
                ServiceWin32,
                ServiceStateAll,
                IntPtr.Zero,
                0,
                out bytesNeeded,
                out servicesReturned,
                ref resumeHandle,
                null);

            if (bytesNeeded == 0)
            {
                return items;
            }

            uint allocSize = bytesNeeded + 4096;
            enumBuffer = Marshal.AllocHGlobal((int)allocSize);
            configBuffer = Marshal.AllocHGlobal((int)configBufferSize);
            descBuffer = Marshal.AllocHGlobal((int)descBufferSize);
            resumeHandle = 0;

            int entrySize = Marshal.SizeOf<EnumServiceStatusProcess>();

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                bool ok = EnumServicesStatusExW(
                    scm,
                    ScEnumProcessInfo,
                    ServiceWin32,
                    ServiceStateAll,
                    enumBuffer,
                    allocSize,
                    out bytesNeeded,
                    out servicesReturned,
                    ref resumeHandle,
                    null);

                int err = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && err != ErrorMoreData)
                {
                    break;
                }

                for (int i = 0; i < servicesReturned; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    IntPtr ptr = IntPtr.Add(enumBuffer, i * entrySize);
                    var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(ptr);

                    string serviceName = Marshal.PtrToStringUni(entry.lpServiceName) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(serviceName))
                    {
                        continue;
                    }

                    string displayName = Marshal.PtrToStringUni(entry.lpDisplayName) ?? serviceName;
                    string status = FormatServiceState(entry.ServiceStatusProcess.dwCurrentState);
                    bool canStop = entry.ServiceStatusProcess.dwCurrentState == ServiceRunning &&
                                   (entry.ServiceStatusProcess.dwControlsAccepted & ServiceAcceptStop) != 0;

                    (ServiceStartModeOption startMode, string startModeLabel, string? binaryPath, string? description) =
                        QueryServiceMetadata(scm, serviceName, ref configBuffer, ref configBufferSize, ref descBuffer, ref descBufferSize);

                    bool isCritical = CriticalServiceProtection.IsCriticalService(serviceName);

                    items.Add(new ManagedServiceItem(
                        ServiceName: serviceName,
                        DisplayName: displayName,
                        Status: status,
                        StartMode: startMode,
                        StartModeLabel: startModeLabel,
                        CanStop: canStop && !isCritical,
                        IsCriticalProtected: isCritical,
                        BinaryPath: binaryPath,
                        Description: description));
                }

                if (ok)
                {
                    break;
                }
            }
        }
        finally
        {
            if (descBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(descBuffer);
            }

            if (configBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(configBuffer);
            }

            if (enumBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(enumBuffer);
            }

            _ = CloseServiceHandle(scm);
        }

        items.Sort(static (a, b) =>
        {
            bool aRun = string.Equals(a.Status, "Працює", StringComparison.Ordinal);
            bool bRun = string.Equals(b.Status, "Працює", StringComparison.Ordinal);
            int byState = bRun.CompareTo(aRun);
            return byState != 0 ? byState : StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
        });

        return items;
    }

    private static (ServiceStartModeOption Mode, string Label, string? BinaryPath, string? Description) QueryServiceMetadata(
        IntPtr scm,
        string serviceName,
        ref IntPtr configBuffer,
        ref uint configBufferSize,
        ref IntPtr descBuffer,
        ref uint descBufferSize)
    {
        IntPtr svc = OpenServiceW(scm, serviceName, ServiceQueryConfig);
        if (svc == IntPtr.Zero)
        {
            return (ServiceStartModeOption.Manual, "—", null, null);
        }

        try
        {
            ServiceStartModeOption mode = ServiceStartModeOption.Manual;
            string label = "Вручну";
            string? binaryPath = null;

            bool ok = QueryServiceConfigW(svc, configBuffer, configBufferSize, out uint needed);
            if (!ok && Marshal.GetLastWin32Error() == ErrorInsufficientBuffer && needed > configBufferSize)
            {
                Marshal.FreeHGlobal(configBuffer);
                configBufferSize = needed + 256;
                configBuffer = Marshal.AllocHGlobal((int)configBufferSize);
                ok = QueryServiceConfigW(svc, configBuffer, configBufferSize, out _);
            }

            if (ok)
            {
                var cfg = Marshal.PtrToStructure<QueryServiceConfig>(configBuffer);
                (mode, label) = MapStartType(cfg.dwStartType);
                binaryPath = Marshal.PtrToStringUni(cfg.lpBinaryPathName);
            }

            string? description = null;
            bool descOk = QueryServiceConfig2W(svc, ServiceConfigDescription, descBuffer, descBufferSize, out uint descNeeded);
            if (!descOk && Marshal.GetLastWin32Error() == ErrorInsufficientBuffer && descNeeded > descBufferSize)
            {
                Marshal.FreeHGlobal(descBuffer);
                descBufferSize = descNeeded + 256;
                descBuffer = Marshal.AllocHGlobal((int)descBufferSize);
                descOk = QueryServiceConfig2W(svc, ServiceConfigDescription, descBuffer, descBufferSize, out _);
            }

            if (descOk)
            {
                var descStruct = Marshal.PtrToStructure<ServiceDescription>(descBuffer);
                description = Marshal.PtrToStringUni(descStruct.lpDescription);
            }

            return (mode, label, binaryPath, description);
        }
        finally
        {
            _ = CloseServiceHandle(svc);
        }
    }

    private static SystemActionResult ExecuteServiceControl(
        string serviceName,
        ServiceControlAction action,
        CancellationToken ct)
    {
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            return err == ErrorAccessDenied
                ? new SystemActionResult(false, RequiresElevation: true, "Потрібні права адміністратора (UAC) для відкриття диспетчера служб.")
                : new SystemActionResult(false, RequiresElevation: false, $"Не вдалося відкрити диспетчер служб (код {err}).");
        }

        try
        {
            uint desiredAccess = action switch
            {
                ServiceControlAction.Start => ServiceStart | ServiceQueryStatus,
                ServiceControlAction.Stop => ServiceStop | ServiceQueryStatus,
                _ => ServiceStart | ServiceStop | ServiceQueryStatus
            };

            IntPtr svc = OpenServiceW(scm, serviceName, desiredAccess);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return err == ErrorAccessDenied
                    ? new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для керування службою «{serviceName}».")
                    : new SystemActionResult(false, RequiresElevation: false, $"Не вдалося відкрити службу «{serviceName}» (код {err}).");
            }

            try
            {
                return action switch
                {
                    ServiceControlAction.Start => StartServiceCore(svc, serviceName, ct),
                    ServiceControlAction.Stop => StopServiceCore(svc, serviceName, ct),
                    ServiceControlAction.Restart => RestartServiceCore(svc, serviceName, ct),
                    _ => new SystemActionResult(false, RequiresElevation: false, "Невідома дія керування службою.")
                };
            }
            finally
            {
                _ = CloseServiceHandle(svc);
            }
        }
        finally
        {
            _ = CloseServiceHandle(scm);
        }
    }

    private static SystemActionResult ExecuteChangeServiceStartMode(
        string serviceName,
        ServiceStartModeOption startMode)
    {
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            return err == ErrorAccessDenied
                ? new SystemActionResult(false, RequiresElevation: true, "Потрібні права адміністратора (UAC) для зміни конфігурації служб.")
                : new SystemActionResult(false, RequiresElevation: false, $"Не вдалося відкрити диспетчер служб (код {err}).");
        }

        try
        {
            IntPtr svc = OpenServiceW(scm, serviceName, ServiceChangeConfig);
            if (svc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return err == ErrorAccessDenied
                    ? new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для зміни типу запуску «{serviceName}».")
                    : new SystemActionResult(false, RequiresElevation: false, $"Не вдалося відкрити службу «{serviceName}» (код {err}).");
            }

            try
            {
                uint win32StartType = startMode switch
                {
                    ServiceStartModeOption.Automatic => ServiceAutoStart,
                    ServiceStartModeOption.Manual => ServiceDemandStart,
                    ServiceStartModeOption.Disabled => ServiceDisabled,
                    _ => ServiceDemandStart
                };

                bool ok = ChangeServiceConfigW(
                    svc,
                    ServiceNoChange,
                    win32StartType,
                    ServiceNoChange,
                    null,
                    null,
                    IntPtr.Zero,
                    null,
                    null,
                    null,
                    null);

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    return err == ErrorAccessDenied
                        ? new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для зміни типу запуску «{serviceName}».")
                        : new SystemActionResult(false, RequiresElevation: false, $"Не вдалося змінити тип запуску служби «{serviceName}» (код {err}).");
                }

                return new SystemActionResult(
                    true,
                    RequiresElevation: false,
                    $"Тип запуску служби «{serviceName}» змінено на «{FormatStartModeOption(startMode)}».");
            }
            finally
            {
                _ = CloseServiceHandle(svc);
            }
        }
        finally
        {
            _ = CloseServiceHandle(scm);
        }
    }

    private static SystemActionResult StartServiceCore(IntPtr svc, string serviceName, CancellationToken ct)
    {
        if (!StartServiceW(svc, 0, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorServiceAlreadyRunning)
            {
                return new SystemActionResult(true, RequiresElevation: false, $"Служба «{serviceName}» уже працює.");
            }

            if (err == ErrorAccessDenied)
            {
                return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для запуску служби «{serviceName}».");
            }

            return new SystemActionResult(false, RequiresElevation: false, $"Не вдалося запустити службу «{serviceName}» (код {err}).");
        }

        bool reached = WaitForServiceState(svc, ServiceRunning, TimeSpan.FromSeconds(10), ct);
        return reached
            ? new SystemActionResult(true, RequiresElevation: false, $"Службу «{serviceName}» успішно запущено.")
            : new SystemActionResult(true, RequiresElevation: false, $"Надіслано команду запуску служби «{serviceName}» (запуск триває).");
    }

    private static SystemActionResult StopServiceCore(IntPtr svc, string serviceName, CancellationToken ct)
    {
        if (!ControlService(svc, ServiceControlStop, out _))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorServiceNotActive)
            {
                return new SystemActionResult(true, RequiresElevation: false, $"Служба «{serviceName}» уже зупинена.");
            }

            if (err == ErrorAccessDenied)
            {
                return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для зупинки служби «{serviceName}».");
            }

            return new SystemActionResult(false, RequiresElevation: false, $"Не вдалося зупинити службу «{serviceName}» (код {err}).");
        }

        bool reached = WaitForServiceState(svc, ServiceStopped, TimeSpan.FromSeconds(10), ct);
        return reached
            ? new SystemActionResult(true, RequiresElevation: false, $"Службу «{serviceName}» успішно зупинено.")
            : new SystemActionResult(true, RequiresElevation: false, $"Надіслано команду зупинки служби «{serviceName}» (зупинка триває).");
    }

    private static SystemActionResult RestartServiceCore(IntPtr svc, string serviceName, CancellationToken ct)
    {
        var stopResult = StopServiceCore(svc, serviceName, ct);
        if (!stopResult.Succeeded)
        {
            return stopResult;
        }

        var startResult = StartServiceCore(svc, serviceName, ct);
        return startResult.Succeeded
            ? new SystemActionResult(true, RequiresElevation: false, $"Службу «{serviceName}» успішно перезапущено.")
            : startResult;
    }

    private static bool WaitForServiceState(IntPtr svc, uint desiredState, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (QueryServiceStatus(svc, out var status) && status.dwCurrentState == desiredState)
            {
                return true;
            }

            Thread.Sleep(200);
        }

        return QueryServiceStatus(svc, out var finalStatus) && finalStatus.dwCurrentState == desiredState;
    }

    private static (ServiceStartModeOption Mode, string Label) MapStartType(uint startType) => startType switch
    {
        0 or 1 or 2 => (ServiceStartModeOption.Automatic, "Автоматично"),
        3 => (ServiceStartModeOption.Manual, "Вручну"),
        4 => (ServiceStartModeOption.Disabled, "Вимкнено"),
        _ => (ServiceStartModeOption.Manual, "—")
    };

    private static string FormatStartModeOption(ServiceStartModeOption mode) => mode switch
    {
        ServiceStartModeOption.Automatic => "Автоматично",
        ServiceStartModeOption.Manual => "Вручну",
        ServiceStartModeOption.Disabled => "Вимкнено",
        _ => "Вручну"
    };

    private static string FormatServiceState(uint state) => state switch
    {
        ServiceStopped => "Зупинено",
        ServiceStartPending => "Запускається",
        ServiceStopPending => "Зупиняється",
        ServiceRunning => "Працює",
        ServiceContinuePending => "Відновлюється",
        ServicePausePending => "Призупиняється",
        ServicePaused => "Призупинено",
        _ => "Невідомо"
    };

    private static object? ComGet(object? target, string propertyName, params object?[]? args)
    {
        if (target is null)
        {
            return null;
        }

        return target.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            binder: null,
            target: target,
            args: args);
    }

    private static void ComSet(object target, string propertyName, object? value)
    {
        target.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            binder: null,
            target: target,
            args: [value]);
    }

    private static object? ComCall(object target, string methodName, params object?[]? args)
    {
        return target.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod,
            binder: null,
            target: target,
            args: args);
    }

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try
            {
                _ = Marshal.FinalReleaseComObject(comObject);
            }
            catch
            {
                // Best-effort COM release
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public ServiceStatusProcess ServiceStatusProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfig
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public IntPtr lpDescription;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr hSCObject);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusExW(
        IntPtr hSCManager,
        uint infoLevel,
        uint dwServiceType,
        uint dwServiceState,
        IntPtr lpServices,
        uint cbBufSize,
        out uint pcbBytesNeeded,
        out uint lpServicesReturned,
        ref uint lpResumeHandle,
        string? pszGroupName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfigW(
        IntPtr hService,
        IntPtr lpServiceConfig,
        uint cbBufSize,
        out uint pcbBytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig2W(
        IntPtr hService,
        uint dwInfoLevel,
        IntPtr lpBuffer,
        uint cbBufSize,
        out uint pcbBytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(
        IntPtr hService,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string? lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword,
        string? lpDisplayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(IntPtr hService, uint dwControl, out ServiceStatus lpServiceStatus);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(IntPtr hService, out ServiceStatus lpServiceStatus);
}
