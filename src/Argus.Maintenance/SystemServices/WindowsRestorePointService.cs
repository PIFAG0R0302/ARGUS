using System.Management;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.System;
using Argus.Core.Journal;
using Microsoft.Extensions.Logging;

namespace Argus.Maintenance.SystemServices;

/// <summary>
/// Creates Windows System Restore points through standard WMI (root\default:SystemRestore).
/// </summary>
public sealed class WindowsRestorePointService : IRestorePointService
{
    private const uint RestorePointApplicationInstall = 0;
    private const uint BeginSystemChangeEvent = 100;

    private readonly IPrivilegeService _privilegeService;
    private readonly IOperationJournal _journal;
    private readonly ILogger<WindowsRestorePointService> _logger;
    private readonly Func<string, uint> _wmiInvoker;

    public WindowsRestorePointService(
        IPrivilegeService privilegeService,
        IOperationJournal journal,
        ILogger<WindowsRestorePointService> logger,
        Func<string, uint>? wmiInvoker = null)
    {
        ArgumentNullException.ThrowIfNull(privilegeService);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(logger);

        _privilegeService = privilegeService;
        _journal = journal;
        _logger = logger;
        _wmiInvoker = wmiInvoker ?? InvokeWmiCreateRestorePoint;
    }

    public async Task<RestorePointResult> CreateRestorePointAsync(
        string description,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        string safeDescription = description.Trim();
        if (safeDescription.Length > 256)
        {
            safeDescription = safeDescription[..256];
        }

        if (!_privilegeService.IsAdministrator)
        {
            const string msg = "Потрібні права адміністратора для створення контрольної точки відновлення Windows.";
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "RestorePointSkipped",
                msg,
                succeeded: false,
                cancellationToken).ConfigureAwait(false);
            return new RestorePointResult(false, safeDescription, msg);
        }

        try
        {
            uint returnCode = await Task.Run(() => _wmiInvoker(safeDescription), cancellationToken).ConfigureAwait(false);
            if (returnCode == 0)
            {
                const string okMsg = "Контрольну точку відновлення Windows успішно створено.";
                await _journal.RecordAsync(
                    "argus.maintenance",
                    OperationSeverity.Success,
                    "RestorePointCreated",
                    safeDescription,
                    succeeded: true,
                    cancellationToken).ConfigureAwait(false);
                return new RestorePointResult(true, safeDescription, okMsg);
            }

            string errMsg = DescribeWmiCode(returnCode);
            _logger.LogWarning("SystemRestore.CreateRestorePoint returned code {Code}: {Message}", returnCode, errMsg);
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Warning,
                "RestorePointFailed",
                $"Код {returnCode}: {errMsg}",
                succeeded: false,
                cancellationToken).ConfigureAwait(false);
            return new RestorePointResult(false, safeDescription, errMsg);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string errMsg = $"Не вдалося викликати службу відновлення Windows: {ex.Message}";
            _logger.LogError(ex, "System Restore invocation failed.");
            await _journal.RecordAsync(
                "argus.maintenance",
                OperationSeverity.Error,
                "RestorePointError",
                errMsg,
                succeeded: false,
                cancellationToken).ConfigureAwait(false);
            return new RestorePointResult(false, safeDescription, errMsg);
        }
    }

    public static string DescribeWmiCode(uint code) => code switch
    {
        0 => "Успішно.",
        5 => "Відмовлено в доступі (перевірте права адміністратора).",
        1058 => "Служба відновлення системи (Захист системи) вимкнена в Windows.",
        1359 => "Внутрішня помилка служби відновлення або досягнуто 24-годинний ліміт Windows на створення точок.",
        _ => $"Windows повернула код помилки {code}."
    };

    private static uint InvokeWmiCreateRestorePoint(string description)
    {
        var scope = new ManagementScope(@"\\.\root\default");
        scope.Connect();

        using var restoreClass = new ManagementClass(scope, new ManagementPath("SystemRestore"), new ObjectGetOptions());
        using var inParams = restoreClass.GetMethodParameters("CreateRestorePoint");
        inParams["Description"] = description;
        inParams["RestorePointType"] = RestorePointApplicationInstall;
        inParams["EventType"] = BeginSystemChangeEvent;

        using var outParams = restoreClass.InvokeMethod("CreateRestorePoint", inParams, null);
        object? retVal = outParams?["ReturnValue"];
        return retVal is null ? 1359u : Convert.ToUInt32(retVal, System.Globalization.CultureInfo.InvariantCulture);
    }
}
