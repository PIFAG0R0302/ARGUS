using Argus.App.Services;
using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class NetworkSuspicionViewModelTests
{
    private sealed class FakeSettingsStore : IArgusSettingsStore
    {
        public string SettingsFilePath => "memory://settings.json";
        public ArgusSettings Current { get; private set; } = new ArgusSettings().Normalize();
        public Task<ArgusSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task SaveAsync(ArgusSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings.Normalize();
            return Task.CompletedTask;
        }
    }

    private sealed class StubPrivilege(bool isAdmin) : IPrivilegeService
    {
        public bool IsAdministrator { get; } = isAdmin;
        public bool TryRelaunchElevated(string[]? arguments = null) => true;
    }

    private sealed class RecordingNetworkService : INetworkManagerService
    {
        public string? LastBlockedIp { get; private set; }
        public string? LastBlockedAppPath { get; private set; }

        public Task<IReadOnlyList<ActiveConnectionItem>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActiveConnectionItem>>(
            [
                new("TCP", "192.168.1.10", 51000, "142.250.185.78", 443, "ESTABLISHED", 4000, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe"),
                new("TCP", "192.168.1.10", 52000, "45.33.32.156", 3333, "ESTABLISHED", 9123, "unknown_miner", null,
                    ConnectionSuspicionLevel.Suspicious,
                    ConnectionSuspicionFlags.DangerousPort | ConnectionSuspicionFlags.HiddenOrUnknownProcess,
                    "Небезпечний порт (майнінг/C2/IRC): 3333")
            ]);

        public Task<IReadOnlyList<FirewallRuleItem>> GetFirewallRulesAsync(bool argusOnly = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirewallRuleItem>>([]);

        public Task<SystemActionResult> BlockApplicationAsync(string appName, string executablePath, FirewallRuleDirection direction = FirewallRuleDirection.Outbound, CancellationToken cancellationToken = default)
        {
            LastBlockedAppPath = executablePath;
            return Task.FromResult(new SystemActionResult(true, false, "Blocked app"));
        }

        public Task<SystemActionResult> BlockRemoteIpAsync(string remoteIp, string? reason = null, FirewallRuleDirection direction = FirewallRuleDirection.Outbound, CancellationToken cancellationToken = default)
        {
            LastBlockedIp = remoteIp;
            return Task.FromResult(new SystemActionResult(true, false, $"Blocked IP {remoteIp}"));
        }

        public Task<SystemActionResult> CreateFirewallRuleAsync(FirewallRuleCreateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Created"));

        public Task<SystemActionResult> SetFirewallRuleEnabledAsync(string ruleName, bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Toggled"));

        public Task<SystemActionResult> DeleteArgusFirewallRuleAsync(string ruleName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Deleted"));
    }

    private sealed class RecordingProcessMonitor : IProcessMonitorService
    {
        public List<int> KilledPids { get; } = [];
        public Task<IReadOnlyList<ProcessMetricsItem>> GetProcessMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessMetricsItem>>([]);
        public Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default)
        {
            KilledPids.Add(pid);
            return Task.FromResult(new SystemActionResult(true, false, $"Killed {pid}"));
        }
        public bool OpenProcessFolder(string? executablePath) => false;
    }

    [Fact]
    public async Task NetworkViewModel_FiltersSuspicious_BlocksPathlessByIp_AndConfirmsProcessKill()
    {
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), new StubPrivilege(true), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var netSvc = new RecordingNetworkService();
        var procSvc = new RecordingProcessMonitor();
        using var vm = new NetworkViewModel(netSvc, uiMode: uiMode, autoLoadOnInit: false, processMonitorService: procSvc);

        await vm.RefreshAsync();
        Assert.Equal(2, vm.Connections.Count);
        Assert.Equal(1, vm.SuspiciousConnectionCount);

        // Toggle "Лише підозрілі"
        vm.ToggleOnlySuspiciousCommand.Execute(null);
        Assert.True(vm.OnlySuspiciousConnections);
        var suspiciousRow = Assert.Single(vm.Connections);
        Assert.Equal("unknown_miner", suspiciousRow.ProcessName);
        Assert.True(suspiciousRow.IsSuspicious);
        Assert.Equal("Підозріле", suspiciousRow.SuspicionBadgeText);
        Assert.Equal("Блок IP", suspiciousRow.BlockButtonLabel);

        // Blocking a pathless suspicious connection blocks by Remote IP
        suspiciousRow.BlockAppCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal("45.33.32.156", netSvc.LastBlockedIp);

        // Requesting terminate opens mandatory confirmation before killing
        suspiciousRow.RequestTerminateCommand.Execute(null);
        Assert.True(vm.IsTerminateConfirmationOpen);
        Assert.Empty(procSvc.KilledPids);

        await vm.ConfirmTerminateConnectionProcessAsync();
        Assert.False(vm.IsTerminateConfirmationOpen);
        Assert.Equal([9123], procSvc.KilledPids);
    }
}
