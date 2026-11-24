using Argus.App.Services;
using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Argus.Maintenance.SystemServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class NetworkSortTests
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

    private sealed class MutableNetworkManagerService : INetworkManagerService
    {
        public IReadOnlyList<ActiveConnectionItem> Connections { get; set; } = [];

        public Task<IReadOnlyList<ActiveConnectionItem>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Connections);

        public Task<IReadOnlyList<FirewallRuleItem>> GetFirewallRulesAsync(bool argusOnly = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirewallRuleItem>>([]);

        public Task<SystemActionResult> BlockApplicationAsync(
            string appName,
            string executablePath,
            FirewallRuleDirection direction = FirewallRuleDirection.Outbound,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Blocked"));

        public Task<SystemActionResult> CreateFirewallRuleAsync(FirewallRuleCreateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Created"));

        public Task<SystemActionResult> SetFirewallRuleEnabledAsync(string ruleName, bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Toggled"));

        public Task<SystemActionResult> DeleteArgusFirewallRuleAsync(string ruleName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Deleted"));
    }

    private sealed class MutableTrafficService : IProcessNetworkTrafficService
    {
        public IReadOnlyList<ProcessNetworkTrafficItem> Processes { get; set; } = [];
        public bool IsEtwActive => true;
        public void Start() { }
        public void Stop() { }
        public void ResetTotals() { }
        public void Dispose() { }

        public Task<NetworkTrafficSnapshot> GetSnapshotAsync(
            IReadOnlyList<ActiveConnectionItem>? currentConnections = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new NetworkTrafficSnapshot(
                NetworkTrafficSourceKind.NativeEtwKernel,
                IsEtwActive: true,
                SourceSummary: "Test ETW",
                Processes: Processes,
                Timestamp: DateTimeOffset.UtcNow));
    }

    private static AppUiModeService CreateUiMode() =>
        new(new FakeSettingsStore(), new StubPrivilege(false), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));

    [Fact]
    public async Task ConnectionGroups_AlphabeticalSort_RemainsStableWhenActivityFluctuates()
    {
        using var uiMode = CreateUiMode();
        var net = new MutableNetworkManagerService
        {
            Connections =
            [
                new("TCP", "192.168.1.10", 51001, "142.250.185.78", 443, "ESTABLISHED", 1001, "Zoom", @"C:\Apps\Zoom\Zoom.exe"),
                new("TCP", "192.168.1.10", 51002, "142.250.185.79", 443, "ESTABLISHED", 1001, "Zoom", @"C:\Apps\Zoom\Zoom.exe"),
                new("TCP", "192.168.1.10", 51003, "104.18.32.47", 443, "LISTENING", 2001, "alpha", @"C:\Apps\Alpha\alpha.exe"),
                new("TCP", "192.168.1.10", 51004, "185.220.101.5", 3333, "ESTABLISHED", 3001, "Chrome", @"C:\Program Files\Google\Chrome\chrome.exe",
                    ConnectionSuspicionLevel.Suspicious, ConnectionSuspicionFlags.DangerousPort, "Небезпечний порт 3333")
            ]
        };

        using var vm = new NetworkViewModel(net, uiMode: uiMode, autoLoadOnInit: false);
        vm.SetGroupSortModeCommand.Execute("ALPHABETICAL");
        await vm.RefreshAsync();

        Assert.True(vm.IsGroupSortAlphabetical);
        Assert.False(vm.IsGroupSortActivity);
        Assert.False(vm.IsGroupSortSystem);
        Assert.Equal(["alpha", "chrome", "zoom"], vm.ConnectionGroups.Select(static g => g.GroupKey).ToArray());

        var initialInstances = vm.ConnectionGroups.ToArray();

        // Activity fluctuates: Zoom drops to 1 LISTENING socket, alpha surges to 3 ESTABLISHED sockets
        net.Connections =
        [
            new("TCP", "192.168.1.10", 51003, "104.18.32.47", 443, "ESTABLISHED", 2001, "alpha", @"C:\Apps\Alpha\alpha.exe"),
            new("TCP", "192.168.1.10", 51010, "104.18.32.48", 443, "ESTABLISHED", 2001, "alpha", @"C:\Apps\Alpha\alpha.exe"),
            new("TCP", "192.168.1.10", 51011, "104.18.32.49", 443, "ESTABLISHED", 2001, "alpha", @"C:\Apps\Alpha\alpha.exe"),
            new("TCP", "192.168.1.10", 51001, "142.250.185.78", 443, "LISTENING", 1001, "Zoom", @"C:\Apps\Zoom\Zoom.exe"),
            new("TCP", "192.168.1.10", 51004, "185.220.101.5", 3333, "ESTABLISHED", 3001, "Chrome", @"C:\Program Files\Google\Chrome\chrome.exe")
        ];

        await vm.RefreshAsync();

        Assert.Equal(["alpha", "chrome", "zoom"], vm.ConnectionGroups.Select(static g => g.GroupKey).ToArray());
        for (int i = 0; i < initialInstances.Length; i++)
        {
            Assert.Same(initialInstances[i], vm.ConnectionGroups[i]);
        }
    }

    [Fact]
    public async Task ConnectionGroups_ActivitySort_OrdersSuspiciousThenEstablishedThenAlphabetical()
    {
        using var uiMode = CreateUiMode();
        var net = new MutableNetworkManagerService
        {
            Connections =
            [
                // Equal activity (1 ESTABLISHED): Bravo and Alpha -> tie-break strictly alphabetical
                new("TCP", "192.168.1.10", 52001, "1.1.1.1", 443, "ESTABLISHED", 2001, "Bravo", @"C:\Apps\Bravo\bravo.exe"),
                new("TCP", "192.168.1.10", 52002, "1.1.1.2", 443, "ESTABLISHED", 2002, "Alpha", @"C:\Apps\Alpha\alpha.exe"),
                // Higher activity (2 ESTABLISHED): Delta
                new("TCP", "192.168.1.10", 52003, "1.1.1.3", 443, "ESTABLISHED", 2003, "Delta", @"C:\Apps\Delta\delta.exe"),
                new("TCP", "192.168.1.10", 52004, "1.1.1.4", 443, "ESTABLISHED", 2003, "Delta", @"C:\Apps\Delta\delta.exe"),
                // Suspicious with 1 LISTENING socket: Zebra -> must still be first
                new("TCP", "192.168.1.10", 52005, "45.33.32.156", 4444, "LISTENING", 2004, "Zebra", @"C:\Users\Public\zebra.exe",
                    ConnectionSuspicionLevel.Suspicious, ConnectionSuspicionFlags.DangerousPort, "Небезпечний порт 4444")
            ]
        };

        using var vm = new NetworkViewModel(net, uiMode: uiMode, autoLoadOnInit: false);
        Assert.True(vm.IsGroupSortActivity);

        await vm.RefreshAsync();

        Assert.Equal(["zebra", "delta", "alpha", "bravo"], vm.ConnectionGroups.Select(static g => g.GroupKey).ToArray());

        // Shuffling input order on next tick must not reorder equal-activity groups
        net.Connections = net.Connections.Reverse().ToList();
        await vm.RefreshAsync();

        Assert.Equal(["zebra", "delta", "alpha", "bravo"], vm.ConnectionGroups.Select(static g => g.GroupKey).ToArray());
    }

    [Fact]
    public async Task ConnectionGroups_SystemSort_PlacesUserAppsFirstAndSystemProcessesSecondAlphabetically()
    {
        using var uiMode = CreateUiMode();
        var net = new MutableNetworkManagerService
        {
            Connections =
            [
                new("TCP", "192.168.1.10", 53001, "20.190.159.1", 443, "ESTABLISHED", 900, "svchost", @"C:\Windows\System32\svchost.exe"),
                new("TCP", "192.168.1.10", 53002, "142.250.185.78", 443, "ESTABLISHED", 4001, "Discord", @"C:\Users\User\AppData\Local\Discord\Discord.exe"),
                new("TCP", "192.168.1.10", 53003, "13.107.4.50", 443, "ESTABLISHED", 4, "System", null),
                new("TCP", "192.168.1.10", 53004, "142.250.185.79", 443, "ESTABLISHED", 4002, "Brave", @"C:\Program Files\BraveSoftware\Brave-Browser\brave.exe"),
                new("TCP", "192.168.1.10", 53005, "20.190.159.2", 443, "ESTABLISHED", 5100, "ProtectedSvc", null),
                new("TCP", "192.168.1.10", 53006, "20.190.159.3", 443, "ESTABLISHED", 1500, "explorer", @"C:\Windows\explorer.exe")
            ]
        };

        using var vm = new NetworkViewModel(net, uiMode: uiMode, autoLoadOnInit: false);
        vm.SetGroupSortModeCommand.Execute("SYSTEM");
        await vm.RefreshAsync();

        Assert.True(vm.IsGroupSortSystem);
        Assert.Equal(
            ["brave", "discord", "explorer", "protectedsvc", "svchost", "system"],
            vm.ConnectionGroups.Select(static g => g.GroupKey).ToArray());

        Assert.False(vm.ConnectionGroups[0].IsSystemProcess);
        Assert.False(vm.ConnectionGroups[1].IsSystemProcess);
        Assert.True(vm.ConnectionGroups[2].IsSystemProcess);
        Assert.True(vm.ConnectionGroups[3].IsSystemProcess);
        Assert.True(vm.ConnectionGroups[4].IsSystemProcess);
        Assert.True(vm.ConnectionGroups[5].IsSystemProcess);
    }

    [Fact]
    public async Task ConnectionGroups_ChildConnections_AreDeterministicallySortedAndSyncedInPlace()
    {
        using var uiMode = CreateUiMode();
        var items = new ActiveConnectionItem[]
        {
            new("UDP", "192.168.1.10", 54002, "*", 0, "LISTENING", 1002, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe"),
            new("TCP", "192.168.1.10", 54002, "142.250.185.80", 443, "ESTABLISHED", 1002, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe"),
            new("TCP", "192.168.1.10", 54001, "142.250.185.79", 443, "ESTABLISHED", 1001, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe"),
            new("TCP", "192.168.1.10", 54001, "142.250.185.78", 443, "SYN_SENT", 1001, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe")
        };

        var net = new MutableNetworkManagerService { Connections = items };
        using var vm = new NetworkViewModel(net, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        var group = Assert.Single(vm.ConnectionGroups);
        var expectedKeys = new[]
        {
            "TCP|192.168.1.10:54001|142.250.185.78:443|1001",
            "TCP|192.168.1.10:54001|142.250.185.79:443|1001",
            "TCP|192.168.1.10:54002|142.250.185.80:443|1002",
            "UDP|192.168.1.10:54002|*:0|1002"
        };

        Assert.Equal(expectedKeys, group.Connections.Select(static c => c.ConnectionKey).ToArray());
        var initialChildren = group.Connections.ToArray();

        // Reverse order on refresh and change state of first item: order and row instances must remain identical
        net.Connections =
        [
            items[0],
            items[1],
            items[2],
            items[3] with { State = "ESTABLISHED" }
        ];
        net.Connections = net.Connections.Reverse().ToList();

        await vm.RefreshAsync();

        Assert.Equal(expectedKeys, group.Connections.Select(static c => c.ConnectionKey).ToArray());
        for (int i = 0; i < initialChildren.Length; i++)
        {
            Assert.Same(initialChildren[i], group.Connections[i]);
        }
        Assert.Equal("ESTABLISHED", group.Connections[0].State);
    }

    [Fact]
    public async Task ProcessTrafficService_ReturnsDeterministicStableOrderWhenThroughputIsZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var net = new MutableNetworkManagerService();
        using var traffic = new WindowsProcessNetworkTrafficService(
            new StubPrivilege(isAdmin: false),
            net,
            NullLogger<WindowsProcessNetworkTrafficService>.Instance);

        // Use non-existent high PIDs so fallback I/O stays at 0 B/s across ticks
        var tick1Connections = new ActiveConnectionItem[]
        {
            new("TCP", "192.168.1.10", 55001, "1.1.1.1", 443, "ESTABLISHED", 900004, "svchost", @"C:\Windows\System32\svchost.exe"),
            new("TCP", "192.168.1.10", 55002, "1.1.1.2", 443, "ESTABLISHED", 900002, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe"),
            new("TCP", "192.168.1.10", 55003, "1.1.1.3", 443, "ESTABLISHED", 900001, "svchost", @"C:\Windows\System32\svchost.exe"),
            new("TCP", "192.168.1.10", 55004, "1.1.1.4", 443, "ESTABLISHED", 900005, "Alpha", @"C:\Apps\Alpha\alpha.exe"),
            new("TCP", "192.168.1.10", 55005, "1.1.1.5", 443, "ESTABLISHED", 900003, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe")
        };

        var snap1 = await traffic.GetSnapshotAsync(tick1Connections, ct);
        var snap2 = await traffic.GetSnapshotAsync(tick1Connections.Reverse().ToList(), ct);

        int[] expectedPids = [900005, 900002, 900003, 900001, 900004];
        Assert.Equal(expectedPids, snap1.Processes.Select(static p => p.Pid).ToArray());
        Assert.Equal(expectedPids, snap2.Processes.Select(static p => p.Pid).ToArray());
    }

    [Fact]
    public async Task ProcessTrafficViewModel_MaintainsStableTieBreakingAcrossSortModes()
    {
        using var uiMode = CreateUiMode();
        var net = new MutableNetworkManagerService();
        var traffic = new MutableTrafficService
        {
            Processes =
            [
                new(5002, "svchost", @"C:\Windows\System32\svchost.exe", 0.0, 0.0, 0, 0, 2, NetworkTrafficSourceKind.NativeEtwKernel),
                new(3001, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe", 0.0, 0.0, 0, 0, 1, NetworkTrafficSourceKind.NativeEtwKernel),
                new(5001, "svchost", @"C:\Windows\System32\svchost.exe", 0.0, 0.0, 0, 0, 4, NetworkTrafficSourceKind.NativeEtwKernel),
                new(1001, "Alpha", @"C:\Apps\Alpha\alpha.exe", 0.0, 0.0, 0, 0, 1, NetworkTrafficSourceKind.NativeEtwKernel)
            ]
        };

        using var vm = new NetworkViewModel(net, uiMode: uiMode, autoLoadOnInit: false, trafficService: traffic);
        await vm.RefreshAsync();

        int[] expectedZeroOrder = [1001, 3001, 5001, 5002];
        Assert.Equal(expectedZeroOrder, vm.ProcessTraffic.Select(static p => p.Pid).ToArray());
        var initialRows = vm.ProcessTraffic.ToArray();

        // Shuffle input on next refresh -> order & VM instances stay identical
        traffic.Processes = traffic.Processes.Reverse().ToList();
        await vm.RefreshAsync();

        Assert.Equal(expectedZeroOrder, vm.ProcessTraffic.Select(static p => p.Pid).ToArray());
        for (int i = 0; i < initialRows.Length; i++)
        {
            Assert.Same(initialRows[i], vm.ProcessTraffic[i]);
        }

        // Switching to UP and TOTAL sort modes also preserves deterministic ProcessName -> Pid tie-breaking
        vm.SetTrafficSortCommand.Execute("UP");
        Assert.Equal(expectedZeroOrder, vm.ProcessTraffic.Select(static p => p.Pid).ToArray());

        vm.SetTrafficSortCommand.Execute("TOTAL");
        Assert.Equal(expectedZeroOrder, vm.ProcessTraffic.Select(static p => p.Pid).ToArray());
    }
}
