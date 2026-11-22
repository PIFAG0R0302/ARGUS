using Argus.Core.Contracts.System;
using Argus.Core.Journal;
using Argus.Maintenance.SystemServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Core.Tests;

public sealed class ConnectionHeuristicsTests
{
    private sealed class StubPrivilege(bool isAdmin) : IPrivilegeService
    {
        public bool IsAdministrator { get; } = isAdmin;
        public bool TryRelaunchElevated(string[]? arguments = null) => false;
    }

    private sealed class StubJournal : IOperationJournal
    {
        public Task RecordAsync(string moduleId, OperationSeverity severity, string action, string details, bool succeeded = true, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public IReadOnlyList<OperationJournalEntry> GetRecent(int maxCount = 200) => [];
    }

    [Fact]
    public void CleanBrowserHttpsConnection_IsNotFlagged()
    {
        var conn = new ActiveConnectionItem(
            "TCP", "192.168.1.10", 51234, "142.250.185.78", 443, "ESTABLISHED", 4000,
            "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe");

        var eval = ConnectionHeuristicAnalyzer.Evaluate(conn, isAdministrator: false, hasReverseDnsPtr: false);
        Assert.Equal(ConnectionSuspicionLevel.None, eval.Level);
        Assert.Equal(ConnectionSuspicionFlags.None, eval.Flags);
        Assert.Null(eval.Summary);
    }

    [Fact]
    public void MasqueradingSvchostInPublicFolder_IsFlaggedHighSuspicious()
    {
        var conn = new ActiveConnectionItem(
            "TCP", "192.168.1.10", 52000, "185.100.87.1", 443, "ESTABLISHED", 6666,
            "svchost", @"C:\Users\Public\svchost.exe");

        var eval = ConnectionHeuristicAnalyzer.Evaluate(conn, isAdministrator: false);
        Assert.Equal(ConnectionSuspicionLevel.Suspicious, eval.Level);
        Assert.True(eval.Flags.HasFlag(ConnectionSuspicionFlags.SystemProcessMasquerading));
        Assert.True(eval.Flags.HasFlag(ConnectionSuspicionFlags.TempOrPublicPath));
        Assert.NotNull(eval.Summary);
    }

    [Theory]
    [InlineData(3333)]
    [InlineData(4444)]
    [InlineData(14444)]
    [InlineData(6667)]
    public void MiningOrC2Port_IsFlaggedHighSuspicious(int remotePort)
    {
        var conn = new ActiveConnectionItem(
            "TCP", "192.168.1.10", 53000, "45.33.32.156", remotePort, "ESTABLISHED", 5123,
            "worker", @"C:\Program Files\SomeApp\worker.exe");

        var eval = ConnectionHeuristicAnalyzer.Evaluate(conn, isAdministrator: false);
        Assert.Equal(ConnectionSuspicionLevel.Suspicious, eval.Level);
        Assert.True(eval.Flags.HasFlag(ConnectionSuspicionFlags.DangerousPort));
    }

    [Fact]
    public void DriveRootExecutable_IsFlaggedHighSuspicious()
    {
        var conn = new ActiveConnectionItem(
            "TCP", "192.168.1.10", 54000, "1.1.1.1", 443, "ESTABLISHED", 5555,
            "agent", @"C:\agent.exe");

        var eval = ConnectionHeuristicAnalyzer.Evaluate(conn, isAdministrator: false);
        Assert.Equal(ConnectionSuspicionLevel.Suspicious, eval.Level);
        Assert.True(eval.Flags.HasFlag(ConnectionSuspicionFlags.RootDrivePath));
    }

    [Fact]
    public void AppDataRoamingOnExternalConnection_IsFlaggedWarningOnly()
    {
        var conn = new ActiveConnectionItem(
            "TCP", "192.168.1.10", 54100, "149.154.167.99", 443, "ESTABLISHED", 7000,
            "Messenger", @"C:\Users\User\AppData\Roaming\Messenger\messenger.exe");

        var eval = ConnectionHeuristicAnalyzer.Evaluate(conn, isAdministrator: false);
        Assert.Equal(ConnectionSuspicionLevel.Warning, eval.Level);
        Assert.True(eval.Flags.HasFlag(ConnectionSuspicionFlags.AppDataOrProgramDataPath));
    }

    [Fact]
    public void ExternalNonStandardPortWithoutPtr_IsFlaggedWarning()
    {
        var conn = new ActiveConnectionItem(
            "TCP", "192.168.1.10", 54200, "198.51.100.42", 9001, "ESTABLISHED", 7100,
            "client", @"C:\Program Files\Client\client.exe");

        Assert.True(ConnectionHeuristicAnalyzer.ShouldCheckReverseDns(conn));
        var eval = ConnectionHeuristicAnalyzer.Evaluate(conn, isAdministrator: false, hasReverseDnsPtr: false);
        Assert.Equal(ConnectionSuspicionLevel.Warning, eval.Level);
        Assert.True(eval.Flags.HasFlag(ConnectionSuspicionFlags.ExternalIpWithoutPtr));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.16.1.2", false)]
    [InlineData("192.168.0.17", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("185.199.108.153", true)]
    public void IsExternalPublicIp_DistinguishesPrivateAndPublicRanges(string ip, bool expectedPublic)
    {
        Assert.Equal(expectedPublic, ConnectionHeuristicAnalyzer.IsExternalPublicIp(ip));
    }

    [Fact]
    public async Task BlockRemoteIp_ValidatesInputAndRequiresUacWhenNonAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("ARGUS_Block_IP_Out_185.199.108.153",
            ArgusFirewallNaming.BuildRemoteIpBlockRuleName("185.199.108.153", FirewallRuleDirection.Outbound));

        var mgr = new WindowsNetworkManagerService(
            new StubPrivilege(isAdmin: false),
            new StubJournal(),
            NullLogger<WindowsNetworkManagerService>.Instance);

        var invalid = await mgr.BlockRemoteIpAsync("not-an-ip", cancellationToken: ct);
        Assert.False(invalid.Succeeded);
        Assert.False(invalid.RequiresElevation);

        var needsUac = await mgr.BlockRemoteIpAsync("185.199.108.153", "Suspicious", cancellationToken: ct);
        Assert.False(needsUac.Succeeded);
        Assert.True(needsUac.RequiresElevation);
    }
}
