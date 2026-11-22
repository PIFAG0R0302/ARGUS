namespace Argus.Core.Contracts.System;

public sealed record ActiveConnectionItem(
    string Protocol,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    string State,
    int Pid,
    string ProcessName,
    string? ExecutablePath)
{
    public bool IsLoopback =>
        string.Equals(LocalAddress, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(LocalAddress, "::1", StringComparison.Ordinal) ||
        string.Equals(RemoteAddress, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(RemoteAddress, "::1", StringComparison.Ordinal);
}

public enum FirewallRuleDirection
{
    Inbound,
    Outbound
}

public enum FirewallRuleAction
{
    Allow,
    Block
}

public sealed record FirewallRuleItem(
    string Name,
    string Description,
    FirewallRuleDirection Direction,
    FirewallRuleAction Action,
    bool Enabled,
    string? ApplicationPath,
    string Protocol,
    string LocalPorts,
    string RemotePorts,
    string Profiles,
    bool IsArgusManaged);

public sealed record FirewallRuleCreateRequest(
    string Name,
    FirewallRuleDirection Direction,
    FirewallRuleAction Action,
    string? ApplicationPath,
    string Protocol,
    string? LocalPorts,
    string? RemotePorts,
    string? Description);

/// <summary>
/// Naming policy ensuring every Windows Firewall rule created by ARGUS carries the ARGUS_ prefix
/// and preventing deletion of non-ARGUS system firewall rules.
/// </summary>
public static class ArgusFirewallNaming
{
    public const string RulePrefix = "ARGUS_";

    public static bool IsArgusRule(string? ruleName) =>
        !string.IsNullOrWhiteSpace(ruleName) &&
        ruleName.Trim().StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase);

    public static string EnsurePrefix(string? rawName)
    {
        string trimmed = string.IsNullOrWhiteSpace(rawName) ? "Rule" : rawName.Trim();
        if (trimmed.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return RulePrefix + trimmed[RulePrefix.Length..];
        }

        return RulePrefix + trimmed;
    }

    public static string BuildApplicationBlockRuleName(string appName, FirewallRuleDirection direction)
    {
        string safeApp = string.IsNullOrWhiteSpace(appName)
            ? "App"
            : new string(appName.Trim().Select(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        string dirTag = direction == FirewallRuleDirection.Inbound ? "In" : "Out";
        return $"{RulePrefix}Block_{dirTag}_{safeApp}";
    }
}

public enum NetworkTrafficSourceKind
{
    NativeEtwKernel,
    SocketAndIoEstimate
}

public sealed record ProcessNetworkTrafficItem(
    int Pid,
    string ProcessName,
    string? ExecutablePath,
    double DownloadBytesPerSec,
    double UploadBytesPerSec,
    long TotalReceivedBytes,
    long TotalSentBytes,
    int ActiveConnectionCount,
    NetworkTrafficSourceKind SourceKind)
{
    public double TotalBytesPerSec => DownloadBytesPerSec + UploadBytesPerSec;
    public long TotalTransferredBytes => TotalReceivedBytes + TotalSentBytes;
}

public sealed record NetworkTrafficSnapshot(
    NetworkTrafficSourceKind ActiveSourceKind,
    bool IsEtwActive,
    string SourceSummary,
    IReadOnlyList<ProcessNetworkTrafficItem> Processes,
    DateTimeOffset Timestamp);

public interface IProcessNetworkTrafficService : IDisposable
{
    bool IsEtwActive { get; }
    void Start();
    void Stop();
    void ResetTotals();
    Task<NetworkTrafficSnapshot> GetSnapshotAsync(
        IReadOnlyList<ActiveConnectionItem>? currentConnections = null,
        CancellationToken cancellationToken = default);
}

public interface INetworkManagerService
{
    Task<IReadOnlyList<ActiveConnectionItem>> GetActiveConnectionsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FirewallRuleItem>> GetFirewallRulesAsync(
        bool argusOnly = false,
        CancellationToken cancellationToken = default);

    Task<SystemActionResult> BlockApplicationAsync(
        string appName,
        string executablePath,
        FirewallRuleDirection direction = FirewallRuleDirection.Outbound,
        CancellationToken cancellationToken = default);

    Task<SystemActionResult> CreateFirewallRuleAsync(
        FirewallRuleCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<SystemActionResult> SetFirewallRuleEnabledAsync(
        string ruleName,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<SystemActionResult> DeleteArgusFirewallRuleAsync(
        string ruleName,
        CancellationToken cancellationToken = default);
}
