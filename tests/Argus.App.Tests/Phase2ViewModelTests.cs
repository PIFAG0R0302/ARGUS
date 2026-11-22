using Argus.App.Services;
using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class Phase2ViewModelTests
{
    private sealed class FakeSettingsStore(ArgusSettings? initial = null) : IArgusSettingsStore
    {
        public string SettingsFilePath => "memory://settings.json";
        public ArgusSettings Current { get; private set; } = (initial ?? new ArgusSettings()).Normalize();
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

    private sealed class FakeProcessMonitorService : IProcessMonitorService
    {
        public List<int> TerminatedPids { get; } = [];

        public Task<IReadOnlyList<ProcessMetricsItem>> GetProcessMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessMetricsItem>>(
            [
                new(4, "System", 0.5, 4_000_000, 0, 0, 120, null, IsCriticalSystem: true, IsProtectedAccess: true),
                new(1200, "explorer", 1.2, 95_000_000, 0, 0, 60, "C:\\Windows\\explorer.exe", IsCriticalSystem: true, IsProtectedAccess: false),
                new(5000, "chrome", 18.4, 650_000_000, 12_000, 8_000, 45, "C:\\Program Files\\Google\\Chrome\\chrome.exe", IsCriticalSystem: false, IsProtectedAccess: false),
                new(6000, "notepad", 0.2, 24_000_000, 500_000, 250_000, 4, "C:\\Windows\\System32\\notepad.exe", IsCriticalSystem: false, IsProtectedAccess: false)
            ]);

        public Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default)
        {
            TerminatedPids.Add(pid);
            return Task.FromResult(new SystemActionResult(true, false, $"Terminated {pid}"));
        }

        public bool OpenProcessFolder(string? executablePath) => !string.IsNullOrWhiteSpace(executablePath);
    }

    private sealed class FakeNetworkMgr : INetworkManagerService
    {
        public Task<IReadOnlyList<ActiveConnectionItem>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActiveConnectionItem>>(
            [
                new("TCP", "192.168.1.5", 50123, "142.250.185.78", 443, "ESTABLISHED", 5000, "chrome", "C:\\Program Files\\Google\\Chrome\\chrome.exe")
            ]);

        public Task<IReadOnlyList<FirewallRuleItem>> GetFirewallRulesAsync(bool argusOnly = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirewallRuleItem>>([]);

        public Task<SystemActionResult> BlockApplicationAsync(string appName, string executablePath, FirewallRuleDirection direction = FirewallRuleDirection.Outbound, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Blocked"));

        public Task<SystemActionResult> CreateFirewallRuleAsync(FirewallRuleCreateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Created"));

        public Task<SystemActionResult> SetFirewallRuleEnabledAsync(string ruleName, bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Toggled"));

        public Task<SystemActionResult> DeleteArgusFirewallRuleAsync(string ruleName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Deleted"));
    }

    private sealed class FakeTrafficService : IProcessNetworkTrafficService
    {
        public bool IsEtwActive => true;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int ResetCount { get; private set; }

        public void Start() => StartCount++;
        public void Stop() => StopCount++;
        public void ResetTotals() => ResetCount++;
        public void Dispose() { }

        public Task<NetworkTrafficSnapshot> GetSnapshotAsync(
            IReadOnlyList<ActiveConnectionItem>? currentConnections = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new NetworkTrafficSnapshot(
                NetworkTrafficSourceKind.NativeEtwKernel,
                IsEtwActive: true,
                SourceSummary: "Native ETW",
                Processes:
                [
                    new ProcessNetworkTrafficItem(
                        5000,
                        "chrome",
                        "C:\\Program Files\\Google\\Chrome\\chrome.exe",
                        DownloadBytesPerSec: 256_000,
                        UploadBytesPerSec: 32_000,
                        TotalReceivedBytes: 10_485_760,
                        TotalSentBytes: 1_048_576,
                        ActiveConnectionCount: 4,
                        SourceKind: NetworkTrafficSourceKind.NativeEtwKernel)
                ],
                Timestamp: DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ProcessesViewModel_HidesCriticalInBasicShowsInExpertAndRequiresKillConfirmation()
    {
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), new StubPrivilege(false), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var fakeSvc = new FakeProcessMonitorService();
        using var vm = new ProcessesViewModel(fakeSvc, uiMode: uiMode, autoLoadOnInit: false);

        await vm.RefreshAsync();

        // Basic mode hides System & explorer
        Assert.Equal(4, vm.TotalProcessCount);
        Assert.Equal(2, vm.HiddenCriticalCount);
        Assert.Equal(2, vm.Processes.Count);
        Assert.DoesNotContain(vm.Processes, static p => p.IsCriticalSystem);

        // Sort by Disk I/O puts notepad (750 KB/s) ahead of chrome (20 KB/s)
        vm.SetSortCommand.Execute("DISK");
        Assert.Equal("notepad", vm.Processes[0].Name);

        // Sort by CPU puts chrome (18.4%) ahead of notepad (0.2%)
        vm.SetSortCommand.Execute("CPU");
        Assert.Equal("chrome", vm.Processes[0].Name);

        // Mandatory confirmation before Terminate
        vm.Processes[0].RequestTerminateCommand.Execute(null);
        Assert.True(vm.IsTerminateConfirmationOpen);
        Assert.Empty(fakeSvc.TerminatedPids);

        await vm.ConfirmTerminateAsync();
        Assert.False(vm.IsTerminateConfirmationOpen);
        Assert.Equal([5000], fakeSvc.TerminatedPids);

        // Expert mode shows critical processes with CanTerminate=false
        vm.IsExpertMode = true;
        Assert.Equal(4, vm.Processes.Count);
        var explorer = Assert.Single(vm.Processes, static p => p.Name == "explorer");
        Assert.True(explorer.IsCriticalSystem);
        Assert.False(explorer.CanTerminate);
    }

    [Fact]
    public async Task NetworkViewModelV2_PopulatesPerProcessTrafficAndManagesEtwLifecycle()
    {
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), new StubPrivilege(true), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var traffic = new FakeTrafficService();
        using var vm = new NetworkViewModel(new FakeNetworkMgr(), uiMode: uiMode, autoLoadOnInit: false, trafficService: traffic);

        vm.SetUiActive(true);
        Assert.Equal(1, traffic.StartCount);

        await vm.RefreshAsync();
        Assert.True(vm.IsEtwTrafficActive);
        Assert.Equal(1, vm.TrafficProcessCount);
        Assert.Single(vm.ProcessTraffic);
        Assert.Equal("chrome", vm.ProcessTraffic[0].ProcessName);
        Assert.Contains("4 з'єдн.", vm.ProcessTraffic[0].SocketsText);

        vm.SelectSubTabCommand.Execute("1");
        Assert.True(vm.IsTrafficTab);

        vm.SetUiActive(false);
        Assert.Equal(1, traffic.StopCount);
    }
}
