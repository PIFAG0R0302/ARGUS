using Argus.Core.Contracts.System;
using Argus.Core.Journal;
using Argus.Maintenance.SystemServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Core.Tests;

public sealed class StartupServicesAndNetworkTests
{
    private sealed class StubPrivilegeService(bool isAdmin) : IPrivilegeService
    {
        public bool IsAdministrator { get; } = isAdmin;
        public bool TryRelaunchElevated(string[]? arguments = null) => false;
    }

    private sealed class InMemoryJournal : IOperationJournal
    {
        public List<OperationJournalEntry> Entries { get; } = [];

        public Task RecordAsync(
            string moduleId,
            OperationSeverity severity,
            string action,
            string details,
            bool succeeded = true,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new OperationJournalEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, moduleId, severity, action, details, succeeded));
            return Task.CompletedTask;
        }

        public IReadOnlyList<OperationJournalEntry> GetRecent(int maxCount = 200) =>
            Entries.TakeLast(maxCount).ToArray();
    }

    [Theory]
    [InlineData("RpcSs", true)]
    [InlineData("rpcss", true)]
    [InlineData("WinDefend", true)]
    [InlineData("BFE", true)]
    [InlineData("mpssvc", true)]
    [InlineData("EventLog", true)]
    [InlineData("Schedule", true)]
    [InlineData("DcomLaunch", true)]
    [InlineData("Spooler", false)]
    [InlineData("W32Time", false)]
    [InlineData("", false)]
    public void CriticalServiceProtection_MatchesExpectedCoreServices(string serviceName, bool expectedProtected)
    {
        Assert.Equal(expectedProtected, CriticalServiceProtection.IsCriticalService(serviceName));
    }

    [Fact]
    public void StartupApprovedEncoding_RoundTripsEnabledAndDisabledStates()
    {
        Assert.True(WindowsStartupAndServicesManager.ParseStartupApprovedEnabled(null));
        Assert.True(WindowsStartupAndServicesManager.ParseStartupApprovedEnabled([]));

        byte[] enabledBytes = WindowsStartupAndServicesManager.CreateStartupApprovedBytes(enabled: true);
        Assert.Equal(12, enabledBytes.Length);
        Assert.Equal(0x02, enabledBytes[0]);
        Assert.True(WindowsStartupAndServicesManager.ParseStartupApprovedEnabled(enabledBytes));

        byte[] disabledBytes = WindowsStartupAndServicesManager.CreateStartupApprovedBytes(enabled: false);
        Assert.Equal(12, disabledBytes.Length);
        Assert.Equal(0x03, disabledBytes[0]);
        Assert.False(WindowsStartupAndServicesManager.ParseStartupApprovedEnabled(disabledBytes));
    }

    [Fact]
    public async Task StartupAndServicesManager_HardBlocksCriticalServicesEvenAsAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = new InMemoryJournal();
        var mgr = new WindowsStartupAndServicesManager(
            new StubPrivilegeService(isAdmin: true),
            journal,
            NullLogger<WindowsStartupAndServicesManager>.Instance);

        var stopResult = await mgr.ControlServiceAsync("RpcSs", ServiceControlAction.Stop, ct);
        Assert.False(stopResult.Succeeded);
        Assert.False(stopResult.RequiresElevation);

        var disableResult = await mgr.SetServiceStartModeAsync("WinDefend", ServiceStartModeOption.Disabled, ct);
        Assert.False(disableResult.Succeeded);
        Assert.False(disableResult.RequiresElevation);
        Assert.Equal(2, journal.Entries.Count);
    }

    [Fact]
    public async Task StartupAndServicesManager_RequiresUacWhenNotAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = new InMemoryJournal();
        var mgr = new WindowsStartupAndServicesManager(
            new StubPrivilegeService(isAdmin: false),
            journal,
            NullLogger<WindowsStartupAndServicesManager>.Instance);

        var controlResult = await mgr.ControlServiceAsync("Spooler", ServiceControlAction.Restart, ct);
        Assert.False(controlResult.Succeeded);
        Assert.True(controlResult.RequiresElevation);

        var modeResult = await mgr.SetServiceStartModeAsync("Spooler", ServiceStartModeOption.Manual, ct);
        Assert.False(modeResult.Succeeded);
        Assert.True(modeResult.RequiresElevation);

        var hklmEntry = new StartupEntryItem(
            "HKLM:Test",
            "TestApp",
            "C:\\Test\\app.exe",
            StartupSourceKind.RegistryLocalMachineRun,
            @"HKLM\...\Run",
            IsEnabled: true,
            RequiresElevation: true,
            IsSystemOrMicrosoft: false,
            "Test");
        var toggleResult = await mgr.SetStartupEntryEnabledAsync(hklmEntry, enabled: false, ct);
        Assert.False(toggleResult.Succeeded);
        Assert.True(toggleResult.RequiresElevation);
    }

    [Fact]
    public async Task StartupAndServicesManager_EnumeratesLiveStartupAndServicesWithoutAdmin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var mgr = new WindowsStartupAndServicesManager(
            new StubPrivilegeService(isAdmin: false),
            new InMemoryJournal(),
            NullLogger<WindowsStartupAndServicesManager>.Instance);

        var startup = await mgr.GetStartupEntriesAsync(includeSystemTasks: false, ct);
        var services = await mgr.GetManagedServicesAsync(ct);

        Assert.NotNull(startup);
        Assert.NotEmpty(services);
        Assert.Contains(services, static s => s.IsCriticalProtected);
    }

    [Theory]
    [InlineData("Block_App", "ARGUS_Block_App")]
    [InlineData("ARGUS_Custom", "ARGUS_Custom")]
    [InlineData("argus_lower", "ARGUS_lower")]
    [InlineData("  ", "ARGUS_Rule")]
    public void ArgusFirewallNaming_EnforcesPrefixConsistently(string raw, string expected)
    {
        string result = ArgusFirewallNaming.EnsurePrefix(raw);
        Assert.Equal(expected, result);
        Assert.True(ArgusFirewallNaming.IsArgusRule(result));
    }

    [Fact]
    public async Task NetworkManager_ProtectsNonArgusFirewallRulesAndChecksUac()
    {
        var ct = TestContext.Current.CancellationToken;
        var adminMgr = new WindowsNetworkManagerService(
            new StubPrivilegeService(isAdmin: true),
            new InMemoryJournal(),
            NullLogger<WindowsNetworkManagerService>.Instance);

        var blockedDelete = await adminMgr.DeleteArgusFirewallRuleAsync("Core Networking - DNS (UDP-Out)", ct);
        Assert.False(blockedDelete.Succeeded);
        Assert.False(blockedDelete.RequiresElevation);

        var userMgr = new WindowsNetworkManagerService(
            new StubPrivilegeService(isAdmin: false),
            new InMemoryJournal(),
            NullLogger<WindowsNetworkManagerService>.Instance);

        var deleteUac = await userMgr.DeleteArgusFirewallRuleAsync("ARGUS_Block_Out_Test", ct);
        Assert.False(deleteUac.Succeeded);
        Assert.True(deleteUac.RequiresElevation);

        var createUac = await userMgr.CreateFirewallRuleAsync(new FirewallRuleCreateRequest(
            "TestPort",
            FirewallRuleDirection.Outbound,
            FirewallRuleAction.Block,
            null,
            "TCP",
            null,
            "443",
            null), ct);
        Assert.False(createUac.Succeeded);
        Assert.True(createUac.RequiresElevation);
    }

    [Fact]
    public async Task NetworkManager_EnumeratesLiveConnectionsAndFirewallRulesWithoutAdmin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var mgr = new WindowsNetworkManagerService(
            new StubPrivilegeService(isAdmin: false),
            new InMemoryJournal(),
            NullLogger<WindowsNetworkManagerService>.Instance);

        var connections = await mgr.GetActiveConnectionsAsync(ct);
        var rules = await mgr.GetFirewallRulesAsync(argusOnly: false, ct);

        Assert.NotEmpty(connections);
        Assert.NotEmpty(rules);
    }
}
