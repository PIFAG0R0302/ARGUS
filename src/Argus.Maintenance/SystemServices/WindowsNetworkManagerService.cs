using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Argus.Core.Contracts.System;
using Argus.Core.Journal;
using Microsoft.Extensions.Logging;

namespace Argus.Maintenance.SystemServices;

/// <summary>
/// Native Windows network connections inspector (iphlpapi.dll TCP/UDP v4+v6 with remote endpoints and PID/process path)
/// and Windows Defender Firewall manager (HNetCfg.FwPolicy2 COM) with strict ARGUS_ prefix enforcement and UAC-on-demand.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsNetworkManagerService : INetworkManagerService
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;
    private const int ErrorInsufficientBuffer = 122;

    private const int NetFwRuleDirIn = 1;
    private const int NetFwRuleDirOut = 2;
    private const int NetFwActionBlock = 0;
    private const int NetFwActionAllow = 1;
    private const int NetFwIpProtocolTcp = 6;
    private const int NetFwIpProtocolUdp = 17;
    private const int NetFwIpProtocolAny = 256;
    private const int NetFwProfile2All = 0x7FFFFFFF;

    private readonly IPrivilegeService _privilegeService;
    private readonly IOperationJournal _journal;
    private readonly ILogger<WindowsNetworkManagerService> _logger;

    public WindowsNetworkManagerService(
        IPrivilegeService privilegeService,
        IOperationJournal journal,
        ILogger<WindowsNetworkManagerService> logger)
    {
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IReadOnlyList<ActiveConnectionItem>> GetActiveConnectionsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CollectActiveConnections(cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<FirewallRuleItem>> GetFirewallRulesAsync(
        bool argusOnly = false,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CollectFirewallRules(argusOnly, cancellationToken), cancellationToken);
    }

    public Task<SystemActionResult> BlockApplicationAsync(
        string appName,
        string executablePath,
        FirewallRuleDirection direction = FirewallRuleDirection.Outbound,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Task.FromResult(new SystemActionResult(
                false,
                RequiresElevation: false,
                "Шлях до виконуваного файлу процесу недоступний (системний або захищений процес)."));
        }

        string ruleName = ArgusFirewallNaming.BuildApplicationBlockRuleName(appName, direction);
        string dirLabel = direction == FirewallRuleDirection.Inbound ? "вхідний" : "вихідний";
        var request = new FirewallRuleCreateRequest(
            Name: ruleName,
            Direction: direction,
            Action: FirewallRuleAction.Block,
            ApplicationPath: executablePath.Trim(),
            Protocol: "ANY",
            LocalPorts: null,
            RemotePorts: null,
            Description: $"Правило ARGUS: заблоковано {dirLabel} трафік для {appName}.");

        return CreateFirewallRuleAsync(request, cancellationToken);
    }

    public async Task<SystemActionResult> CreateFirewallRuleAsync(
        FirewallRuleCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_privilegeService.IsAdministrator)
        {
            const string uacMsg = "Потрібні права адміністратора (UAC) для створення правила Windows Firewall.";
            await _journal.RecordAsync(
                "argus.network",
                OperationSeverity.Warning,
                "FirewallCreateRequiresElevation",
                $"Правило={ArgusFirewallNaming.EnsurePrefix(request.Name)}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: true, uacMsg);
        }

        string prefixedName = ArgusFirewallNaming.EnsurePrefix(request.Name);
        var result = await Task.Run(
            () => CreateFirewallRuleCore(prefixedName, request),
            cancellationToken).ConfigureAwait(false);

        await _journal.RecordAsync(
            "argus.network",
            result.Succeeded ? OperationSeverity.Success : OperationSeverity.Warning,
            "FirewallRuleCreated",
            $"Правило={prefixedName}: {result.Message}",
            succeeded: result.Succeeded,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<SystemActionResult> SetFirewallRuleEnabledAsync(
        string ruleName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            return new SystemActionResult(false, RequiresElevation: false, "Не вказано назву правила Firewall.");
        }

        string trimmed = ruleName.Trim();
        if (!_privilegeService.IsAdministrator)
        {
            string uacMsg = $"Потрібні права адміністратора (UAC) для зміни правила «{trimmed}».";
            await _journal.RecordAsync(
                "argus.network",
                OperationSeverity.Warning,
                "FirewallToggleRequiresElevation",
                $"Правило={trimmed}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: true, uacMsg);
        }

        var result = await Task.Run(
            () => ToggleFirewallRuleCore(trimmed, enabled),
            cancellationToken).ConfigureAwait(false);

        await _journal.RecordAsync(
            "argus.network",
            result.Succeeded ? OperationSeverity.Success : OperationSeverity.Warning,
            "FirewallRuleToggled",
            $"Правило={trimmed} -> {(enabled ? "Увімкнено" : "Вимкнено")}: {result.Message}",
            succeeded: result.Succeeded,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<SystemActionResult> DeleteArgusFirewallRuleAsync(
        string ruleName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            return new SystemActionResult(false, RequiresElevation: false, "Не вказано назву правила Firewall.");
        }

        string trimmed = ruleName.Trim();
        if (!ArgusFirewallNaming.IsArgusRule(trimmed))
        {
            string blockedMsg = $"Видалення дозволено лише для правил із префіксом «{ArgusFirewallNaming.RulePrefix}». Штатні правила Windows захищено.";
            await _journal.RecordAsync(
                "argus.network",
                OperationSeverity.Warning,
                "FirewallDeleteBlocked",
                $"Правило={trimmed}: {blockedMsg}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: false, blockedMsg);
        }

        if (!_privilegeService.IsAdministrator)
        {
            string uacMsg = $"Потрібні права адміністратора (UAC) для видалення правила «{trimmed}».";
            await _journal.RecordAsync(
                "argus.network",
                OperationSeverity.Warning,
                "FirewallDeleteRequiresElevation",
                $"Правило={trimmed}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SystemActionResult(false, RequiresElevation: true, uacMsg);
        }

        var result = await Task.Run(
            () => DeleteFirewallRuleCore(trimmed),
            cancellationToken).ConfigureAwait(false);

        await _journal.RecordAsync(
            "argus.network",
            result.Succeeded ? OperationSeverity.Success : OperationSeverity.Warning,
            "FirewallRuleDeleted",
            $"Правило={trimmed}: {result.Message}",
            succeeded: result.Succeeded,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static IReadOnlyList<ActiveConnectionItem> CollectActiveConnections(CancellationToken ct)
    {
        var procMap = BuildProcessLookup();
        var items = new List<ActiveConnectionItem>(256);

        CollectTcp4(items, procMap, ct);
        CollectTcp6(items, procMap, ct);
        CollectUdp4(items, procMap, ct);
        CollectUdp6(items, procMap, ct);

        items.Sort(static (a, b) =>
        {
            int aRank = StateSortRank(a.State);
            int bRank = StateSortRank(b.State);
            int byRank = aRank.CompareTo(bRank);
            if (byRank != 0)
            {
                return byRank;
            }

            int byProc = StringComparer.OrdinalIgnoreCase.Compare(a.ProcessName, b.ProcessName);
            return byProc != 0 ? byProc : a.LocalPort.CompareTo(b.LocalPort);
        });

        return items;
    }

    private static int StateSortRank(string state) => state switch
    {
        "ESTABLISHED" => 0,
        "SYN_SENT" or "SYN_RCVD" => 1,
        "LISTENING" => 2,
        "CLOSE_WAIT" or "TIME_WAIT" => 3,
        _ => 4
    };

    private static Dictionary<int, (string Name, string? ExecutablePath)> BuildProcessLookup()
    {
        var map = new Dictionary<int, (string, string?)>(256);
        Process[] procs = Process.GetProcesses();
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    int pid = p.Id;
                    string name = p.ProcessName;
                    string? path = null;
                    if (pid is not (0 or 4))
                    {
                        try
                        {
                            path = p.MainModule?.FileName;
                        }
                        catch
                        {
                            // Protected or elevated process path is optional
                        }
                    }

                    map[pid] = (name, path);
                }
                catch
                {
                    // Skip transient exited process
                }
            }
        }
        finally
        {
            foreach (var p in procs)
            {
                try
                {
                    p.Dispose();
                }
                catch
                {
                    // Best-effort dispose
                }
            }
        }

        return map;
    }

    private static (string Name, string? Path) ResolveProcess(
        Dictionary<int, (string Name, string? Path)> map,
        int pid)
    {
        if (pid == 0)
        {
            return ("System Idle", null);
        }

        if (pid == 4)
        {
            return ("System", null);
        }

        return map.TryGetValue(pid, out var info) ? info : ($"PID {pid}", null);
    }

    private static void CollectTcp4(
        List<ActiveConnectionItem> target,
        Dictionary<int, (string Name, string? Path)> procMap,
        CancellationToken ct)
    {
        int size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint ret = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
            if (ret == ErrorInsufficientBuffer)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
                ret = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
            }

            if (ret != 0)
            {
                return;
            }

            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize));
                int pid = unchecked((int)row.OwningPid);
                var (procName, exePath) = ResolveProcess(procMap, pid);
                string state = FormatTcpState(row.State);
                string localIp = new IPAddress(row.LocalAddr).ToString();
                int localPort = ConvertNetworkPort(row.LocalPort);
                string remoteIp = row.State == 2 ? "—" : new IPAddress(row.RemoteAddr).ToString();
                int remotePort = row.State == 2 ? 0 : ConvertNetworkPort(row.RemotePort);

                target.Add(new ActiveConnectionItem(
                    Protocol: "TCP",
                    LocalAddress: localIp,
                    LocalPort: localPort,
                    RemoteAddress: remoteIp,
                    RemotePort: remotePort,
                    State: state,
                    Pid: pid,
                    ProcessName: procName,
                    ExecutablePath: exePath));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CollectTcp6(
        List<ActiveConnectionItem> target,
        Dictionary<int, (string Name, string? Path)> procMap,
        CancellationToken ct)
    {
        int size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet6, TcpTableOwnerPidAll, 0);
        if (size <= 0)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint ret = GetExtendedTcpTable(buffer, ref size, true, AfInet6, TcpTableOwnerPidAll, 0);
            if (ret == ErrorInsufficientBuffer)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
                ret = GetExtendedTcpTable(buffer, ref size, true, AfInet6, TcpTableOwnerPidAll, 0);
            }

            if (ret != 0)
            {
                return;
            }

            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();

            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize));
                int pid = unchecked((int)row.OwningPid);
                var (procName, exePath) = ResolveProcess(procMap, pid);
                string state = FormatTcpState(row.State);
                string localIp = new IPAddress(row.LocalAddr).ToString();
                int localPort = ConvertNetworkPort(row.LocalPort);
                string remoteIp = row.State == 2 ? "—" : new IPAddress(row.RemoteAddr).ToString();
                int remotePort = row.State == 2 ? 0 : ConvertNetworkPort(row.RemotePort);

                target.Add(new ActiveConnectionItem(
                    Protocol: "TCPv6",
                    LocalAddress: localIp,
                    LocalPort: localPort,
                    RemoteAddress: remoteIp,
                    RemotePort: remotePort,
                    State: state,
                    Pid: pid,
                    ProcessName: procName,
                    ExecutablePath: exePath));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CollectUdp4(
        List<ActiveConnectionItem> target,
        Dictionary<int, (string Name, string? Path)> procMap,
        CancellationToken ct)
    {
        int size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, true, AfInet, UdpTableOwnerPid, 0);
        if (size <= 0)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint ret = GetExtendedUdpTable(buffer, ref size, true, AfInet, UdpTableOwnerPid, 0);
            if (ret == ErrorInsufficientBuffer)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
                ret = GetExtendedUdpTable(buffer, ref size, true, AfInet, UdpTableOwnerPid, 0);
            }

            if (ret != 0)
            {
                return;
            }

            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();

            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize));
                int pid = unchecked((int)row.OwningPid);
                var (procName, exePath) = ResolveProcess(procMap, pid);

                target.Add(new ActiveConnectionItem(
                    Protocol: "UDP",
                    LocalAddress: new IPAddress(row.LocalAddr).ToString(),
                    LocalPort: ConvertNetworkPort(row.LocalPort),
                    RemoteAddress: "*",
                    RemotePort: 0,
                    State: "UDP",
                    Pid: pid,
                    ProcessName: procName,
                    ExecutablePath: exePath));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void CollectUdp6(
        List<ActiveConnectionItem> target,
        Dictionary<int, (string Name, string? Path)> procMap,
        CancellationToken ct)
    {
        int size = 0;
        _ = GetExtendedUdpTable(IntPtr.Zero, ref size, true, AfInet6, UdpTableOwnerPid, 0);
        if (size <= 0)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint ret = GetExtendedUdpTable(buffer, ref size, true, AfInet6, UdpTableOwnerPid, 0);
            if (ret == ErrorInsufficientBuffer)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
                ret = GetExtendedUdpTable(buffer, ref size, true, AfInet6, UdpTableOwnerPid, 0);
            }

            if (ret != 0)
            {
                return;
            }

            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MibUdp6RowOwnerPid>();

            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(IntPtr.Add(rowPtr, i * rowSize));
                int pid = unchecked((int)row.OwningPid);
                var (procName, exePath) = ResolveProcess(procMap, pid);

                target.Add(new ActiveConnectionItem(
                    Protocol: "UDPv6",
                    LocalAddress: new IPAddress(row.LocalAddr).ToString(),
                    LocalPort: ConvertNetworkPort(row.LocalPort),
                    RemoteAddress: "*",
                    RemotePort: 0,
                    State: "UDP",
                    Pid: pid,
                    ProcessName: procName,
                    ExecutablePath: exePath));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IReadOnlyList<FirewallRuleItem> CollectFirewallRules(bool argusOnly, CancellationToken ct)
    {
        var items = new List<FirewallRuleItem>(128);
        object? fwPolicy = null;
        object? rulesCollection = null;

        try
        {
            Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null)
            {
                return items;
            }

            fwPolicy = Activator.CreateInstance(policyType);
            if (fwPolicy is null)
            {
                return items;
            }

            rulesCollection = ComGet(fwPolicy, "Rules");
            if (rulesCollection is not IEnumerable enumerable)
            {
                return items;
            }

            foreach (object? rule in enumerable)
            {
                ct.ThrowIfCancellationRequested();
                if (rule is null)
                {
                    continue;
                }

                try
                {
                    string name = Convert.ToString(ComGet(rule, "Name"), CultureInfo.InvariantCulture) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    bool isArgus = ArgusFirewallNaming.IsArgusRule(name);
                    if (argusOnly && !isArgus)
                    {
                        continue;
                    }

                    string description = Convert.ToString(ComGet(rule, "Description"), CultureInfo.InvariantCulture) ?? string.Empty;
                    int dirRaw = Convert.ToInt32(ComGet(rule, "Direction"), CultureInfo.InvariantCulture);
                    int actRaw = Convert.ToInt32(ComGet(rule, "Action"), CultureInfo.InvariantCulture);
                    bool enabled = Convert.ToBoolean(ComGet(rule, "Enabled"), CultureInfo.InvariantCulture);
                    string? appPath = Convert.ToString(ComGet(rule, "ApplicationName"), CultureInfo.InvariantCulture);
                    int protoRaw = Convert.ToInt32(ComGet(rule, "Protocol"), CultureInfo.InvariantCulture);
                    string localPorts = Convert.ToString(ComGet(rule, "LocalPorts"), CultureInfo.InvariantCulture) ?? "*";
                    string remotePorts = Convert.ToString(ComGet(rule, "RemotePorts"), CultureInfo.InvariantCulture) ?? "*";
                    int profilesRaw = Convert.ToInt32(ComGet(rule, "Profiles"), CultureInfo.InvariantCulture);

                    items.Add(new FirewallRuleItem(
                        Name: name,
                        Description: description,
                        Direction: dirRaw == NetFwRuleDirIn ? FirewallRuleDirection.Inbound : FirewallRuleDirection.Outbound,
                        Action: actRaw == NetFwActionBlock ? FirewallRuleAction.Block : FirewallRuleAction.Allow,
                        Enabled: enabled,
                        ApplicationPath: string.IsNullOrWhiteSpace(appPath) ? null : appPath,
                        Protocol: FormatFirewallProtocol(protoRaw),
                        LocalPorts: string.IsNullOrWhiteSpace(localPorts) ? "*" : localPorts,
                        RemotePorts: string.IsNullOrWhiteSpace(remotePorts) ? "*" : remotePorts,
                        Profiles: FormatProfiles(profilesRaw),
                        IsArgusManaged: isArgus));
                }
                catch
                {
                    // Skip unreadable rule
                }
                finally
                {
                    ReleaseCom(rule);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Unable to enumerate Windows Firewall rules.");
        }
        finally
        {
            ReleaseCom(rulesCollection);
            ReleaseCom(fwPolicy);
        }

        items.Sort(static (a, b) =>
        {
            int byArgus = b.IsArgusManaged.CompareTo(a.IsArgusManaged);
            if (byArgus != 0)
            {
                return byArgus;
            }

            int byEnabled = b.Enabled.CompareTo(a.Enabled);
            return byEnabled != 0 ? byEnabled : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        return items;
    }

    private SystemActionResult CreateFirewallRuleCore(string prefixedName, FirewallRuleCreateRequest request)
    {
        object? fwPolicy = null;
        object? rules = null;
        object? newRule = null;

        try
        {
            Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            Type? ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule");
            if (policyType is null || ruleType is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, "COM-інтерфейс Windows Firewall недоступний.");
            }

            fwPolicy = Activator.CreateInstance(policyType);
            newRule = Activator.CreateInstance(ruleType);
            if (fwPolicy is null || newRule is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, "Не вдалося створити екземпляр правила Firewall.");
            }

            rules = ComGet(fwPolicy, "Rules");
            if (rules is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, "Не вдалося отримати колекцію правил Firewall.");
            }

            int protocolNumber = ParseFirewallProtocol(request.Protocol);

            ComSet(newRule, "Name", prefixedName);
            ComSet(newRule, "Description", string.IsNullOrWhiteSpace(request.Description) ? "Створено в ARGUS" : request.Description.Trim());
            ComSet(newRule, "Direction", request.Direction == FirewallRuleDirection.Inbound ? NetFwRuleDirIn : NetFwRuleDirOut);
            ComSet(newRule, "Action", request.Action == FirewallRuleAction.Block ? NetFwActionBlock : NetFwActionAllow);
            ComSet(newRule, "Protocol", protocolNumber);
            ComSet(newRule, "Profiles", NetFwProfile2All);
            ComSet(newRule, "Enabled", true);

            if (!string.IsNullOrWhiteSpace(request.ApplicationPath))
            {
                ComSet(newRule, "ApplicationName", request.ApplicationPath.Trim());
            }

            if (protocolNumber is NetFwIpProtocolTcp or NetFwIpProtocolUdp)
            {
                if (!string.IsNullOrWhiteSpace(request.LocalPorts) && request.LocalPorts.Trim() != "*")
                {
                    ComSet(newRule, "LocalPorts", request.LocalPorts.Trim());
                }

                if (!string.IsNullOrWhiteSpace(request.RemotePorts) && request.RemotePorts.Trim() != "*")
                {
                    ComSet(newRule, "RemotePorts", request.RemotePorts.Trim());
                }
            }

            // Remove an existing ARGUS rule with the same name first to avoid duplicates
            try
            {
                ComCall(rules, "Remove", prefixedName);
            }
            catch
            {
                // Ignore if rule does not already exist
            }

            ComCall(rules, "Add", newRule);
            return new SystemActionResult(true, RequiresElevation: false, $"Правило Windows Firewall «{prefixedName}» створено й активовано.");
        }
        catch (UnauthorizedAccessException)
        {
            return new SystemActionResult(false, RequiresElevation: true, "Потрібні права адміністратора (UAC) для створення правила Firewall.");
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            return new SystemActionResult(false, RequiresElevation: true, "Потрібні права адміністратора (UAC) для створення правила Firewall.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is UnauthorizedAccessException or COMException { HResult: unchecked((int)0x80070005) })
        {
            return new SystemActionResult(false, RequiresElevation: true, "Потрібні права адміністратора (UAC) для створення правила Firewall.");
        }
        catch (Exception ex)
        {
            string details = ex is TargetInvocationException tie && tie.InnerException is not null
                ? tie.InnerException.Message
                : ex.Message;
            _logger.LogWarning(ex, "Failed to create firewall rule '{RuleName}'.", prefixedName);
            return new SystemActionResult(false, RequiresElevation: false, $"Не вдалося створити правило «{prefixedName}»: {details}");
        }
        finally
        {
            ReleaseCom(newRule);
            ReleaseCom(rules);
            ReleaseCom(fwPolicy);
        }
    }

    private SystemActionResult ToggleFirewallRuleCore(string ruleName, bool enabled)
    {
        object? fwPolicy = null;
        object? rules = null;
        object? rule = null;

        try
        {
            Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, "COM-інтерфейс Windows Firewall недоступний.");
            }

            fwPolicy = Activator.CreateInstance(policyType);
            rules = ComGet(fwPolicy, "Rules");
            if (rules is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, "Не вдалося отримати колекцію правил Firewall.");
            }

            rule = ComCall(rules, "Item", ruleName);
            if (rule is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, $"Правило «{ruleName}» не знайдено.");
            }

            ComSet(rule, "Enabled", enabled);
            string stateText = enabled ? "увімкнено" : "вимкнено";
            return new SystemActionResult(true, RequiresElevation: false, $"Правило Firewall «{ruleName}» {stateText}.");
        }
        catch (UnauthorizedAccessException)
        {
            return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для зміни правила «{ruleName}».");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is UnauthorizedAccessException or COMException { HResult: unchecked((int)0x80070005) })
        {
            return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для зміни правила «{ruleName}».");
        }
        catch (Exception ex)
        {
            string details = ex is TargetInvocationException tie && tie.InnerException is not null
                ? tie.InnerException.Message
                : ex.Message;
            _logger.LogWarning(ex, "Failed to toggle firewall rule '{RuleName}'.", ruleName);
            return new SystemActionResult(false, RequiresElevation: false, $"Не вдалося змінити правило «{ruleName}»: {details}");
        }
        finally
        {
            ReleaseCom(rule);
            ReleaseCom(rules);
            ReleaseCom(fwPolicy);
        }
    }

    private SystemActionResult DeleteFirewallRuleCore(string ruleName)
    {
        object? fwPolicy = null;
        object? rules = null;

        try
        {
            Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, "COM-інтерфейс Windows Firewall недоступний.");
            }

            fwPolicy = Activator.CreateInstance(policyType);
            rules = ComGet(fwPolicy, "Rules");
            if (rules is null)
            {
                return new SystemActionResult(false, RequiresElevation: false, "Не вдалося отримати колекцію правил Firewall.");
            }

            ComCall(rules, "Remove", ruleName);
            return new SystemActionResult(true, RequiresElevation: false, $"Правило «{ruleName}» видалено з Windows Firewall.");
        }
        catch (UnauthorizedAccessException)
        {
            return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для видалення правила «{ruleName}».");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is UnauthorizedAccessException or COMException { HResult: unchecked((int)0x80070005) })
        {
            return new SystemActionResult(false, RequiresElevation: true, $"Потрібні права адміністратора (UAC) для видалення правила «{ruleName}».");
        }
        catch (Exception ex)
        {
            string details = ex is TargetInvocationException tie && tie.InnerException is not null
                ? tie.InnerException.Message
                : ex.Message;
            _logger.LogWarning(ex, "Failed to delete firewall rule '{RuleName}'.", ruleName);
            return new SystemActionResult(false, RequiresElevation: false, $"Не вдалося видалити правило «{ruleName}»: {details}");
        }
        finally
        {
            ReleaseCom(rules);
            ReleaseCom(fwPolicy);
        }
    }

    internal static int ParseFirewallProtocol(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return NetFwIpProtocolAny;
        }

        return protocol.Trim().ToUpperInvariant() switch
        {
            "TCP" => NetFwIpProtocolTcp,
            "UDP" => NetFwIpProtocolUdp,
            _ => NetFwIpProtocolAny
        };
    }

    private static string FormatFirewallProtocol(int protocol) => protocol switch
    {
        NetFwIpProtocolTcp => "TCP",
        NetFwIpProtocolUdp => "UDP",
        NetFwIpProtocolAny => "ANY",
        1 => "ICMPv4",
        58 => "ICMPv6",
        _ => protocol.ToString(CultureInfo.InvariantCulture)
    };

    private static string FormatProfiles(int profilesMask)
    {
        if (profilesMask == NetFwProfile2All || (profilesMask & 7) == 7)
        {
            return "Усі профілі";
        }

        var parts = new List<string>(3);
        if ((profilesMask & 1) != 0) parts.Add("Домен");
        if ((profilesMask & 2) != 0) parts.Add("Приватна");
        if ((profilesMask & 4) != 0) parts.Add("Публічна");
        return parts.Count > 0 ? string.Join(", ", parts) : "Усі";
    }

    private static int ConvertNetworkPort(uint networkOrderPort)
    {
        return ((int)(networkOrderPort & 0xFF) << 8) | (int)((networkOrderPort >> 8) & 0xFF);
    }

    private static string FormatTcpState(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTENING",
        3 => "SYN_SENT",
        4 => "SYN_RCVD",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => "UNKNOWN"
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
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);
}
