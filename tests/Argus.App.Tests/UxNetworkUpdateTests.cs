using Argus.App.Services;
using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class UxNetworkUpdateTests
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

    private sealed class MutableNetworkManagerService : INetworkManagerService
    {
        public IReadOnlyList<ActiveConnectionItem> Connections { get; set; } = [];
        public List<(string AppName, string ExecutablePath)> BlockedApps { get; } = [];

        public Task<IReadOnlyList<ActiveConnectionItem>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Connections);

        public Task<IReadOnlyList<FirewallRuleItem>> GetFirewallRulesAsync(bool argusOnly = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirewallRuleItem>>([]);

        public Task<SystemActionResult> BlockApplicationAsync(
            string appName,
            string executablePath,
            FirewallRuleDirection direction = FirewallRuleDirection.Outbound,
            CancellationToken cancellationToken = default)
        {
            BlockedApps.Add((appName, executablePath));
            return Task.FromResult(new SystemActionResult(true, false, $"Blocked {appName}"));
        }

        public Task<SystemActionResult> CreateFirewallRuleAsync(FirewallRuleCreateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Created"));

        public Task<SystemActionResult> SetFirewallRuleEnabledAsync(string ruleName, bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Toggled"));

        public Task<SystemActionResult> DeleteArgusFirewallRuleAsync(string ruleName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Deleted"));
    }

    [Fact]
    public async Task ConnectionGroups_GroupCaseInsensitivelyAndStartCollapsedByDefault()
    {
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), new StubPrivilege(false), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var fakeNet = new MutableNetworkManagerService
        {
            Connections =
            [
                new("TCP", "192.168.1.10", 51001, "142.250.185.78", 443, "ESTABLISHED", 1001, "Chrome", @"C:\Program Files\Google\Chrome\chrome.exe"),
                new("TCP", "192.168.1.10", 51002, "142.250.185.79", 443, "ESTABLISHED", 1002, "chrome.exe", @"C:\Program Files\Google\Chrome\chrome.exe"),
                new("UDP", "192.168.1.10", 51003, "*", 0, "LISTENING", 1001, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe"),
                new("TCP", "192.168.1.10", 52001, "104.18.32.47", 443, "ESTABLISHED", 2001, "ChatGPT", @"C:\Apps\ChatGPT\ChatGPT.exe")
            ]
        };

        using var vm = new NetworkViewModel(fakeNet, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        // Flat Connections remains populated for backward compatibility
        Assert.Equal(4, vm.Connections.Count);
        Assert.True(vm.HasConnectionGroups);
        Assert.Equal(2, vm.ConnectionGroups.Count);

        // Groups start collapsed by default
        Assert.All(vm.ConnectionGroups, static g =>
        {
            Assert.False(g.IsExpanded);
            Assert.Equal("▶", g.ExpandGlyph);
        });

        var chatGpt = Assert.Single(vm.ConnectionGroups, static g => g.GroupKey == "chatgpt");
        Assert.Equal(1, chatGpt.ConnectionCount);
        Assert.Equal("1 з'єдн.", chatGpt.ConnectionCountText);
        Assert.False(chatGpt.HasMultiplePids);

        var chrome = Assert.Single(vm.ConnectionGroups, static g => g.GroupKey == "chrome");
        Assert.Equal(3, chrome.ConnectionCount);
        Assert.Equal("3 з'єдн.", chrome.ConnectionCountText);
        Assert.Equal(2, chrome.PidCount);
        Assert.True(chrome.HasMultiplePids);
        Assert.Equal("2 PID", chrome.PidCountText);
        Assert.Equal(2, chrome.EstablishedCount);
        Assert.True(chrome.HasExecutablePath);
        Assert.Equal(3, chrome.Connections.Count);

        // Toggle expand/collapse
        chrome.ToggleExpandedCommand.Execute(null);
        Assert.True(chrome.IsExpanded);
        Assert.Equal("▼", chrome.ExpandGlyph);

        chrome.ToggleExpandedCommand.Execute(null);
        Assert.False(chrome.IsExpanded);
        Assert.Equal("▶", chrome.ExpandGlyph);
    }

    [Fact]
    public async Task ConnectionGroups_AggregateSuspicionAndAutoExpandWhenOnlySuspiciousEnabled()
    {
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), new StubPrivilege(false), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var fakeNet = new MutableNetworkManagerService
        {
            Connections =
            [
                new("TCP", "192.168.1.10", 51001, "142.250.185.78", 443, "ESTABLISHED", 1001, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe"),
                new("TCP", "192.168.1.10", 53001, "185.220.101.5", 4444, "ESTABLISHED", 3001, "miner", @"C:\Users\Public\miner.exe",
                    ConnectionSuspicionLevel.Warning, ConnectionSuspicionFlags.TempOrPublicPath, "Запуск із Temp/Public"),
                new("TCP", "192.168.1.10", 53002, "185.220.101.6", 3333, "ESTABLISHED", 3001, "miner", @"C:\Users\Public\miner.exe",
                    ConnectionSuspicionLevel.Suspicious, ConnectionSuspicionFlags.DangerousPort, "Небезпечний порт 3333")
            ]
        };

        using var vm = new NetworkViewModel(fakeNet, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        // Suspicious group surfaces first and aggregates highest severity + summary
        Assert.Equal(2, vm.ConnectionGroups.Count);
        var minerGroup = vm.ConnectionGroups[0];
        Assert.Equal("miner", minerGroup.GroupKey);
        Assert.True(minerGroup.IsSuspicious);
        Assert.False(minerGroup.IsWarning);
        Assert.True(minerGroup.IsFlagged);
        Assert.Equal("Підозріле", minerGroup.SuspicionBadgeText);
        Assert.Contains("Небезпечний порт 3333", minerGroup.SuspicionSummary);
        Assert.Contains("Запуск із Temp/Public", minerGroup.SuspicionSummary);
        Assert.False(minerGroup.IsExpanded);

        // Enabling "Лише підозрілі" filters to flagged groups and auto-expands them
        vm.ToggleOnlySuspiciousCommand.Execute(null);
        Assert.True(vm.OnlySuspiciousConnections);
        var onlyGroup = Assert.Single(vm.ConnectionGroups);
        Assert.Same(minerGroup, onlyGroup);
        Assert.True(onlyGroup.IsExpanded);
        Assert.Equal("▼", onlyGroup.ExpandGlyph);
    }

    [Fact]
    public async Task ConnectionGroups_SyncInPlacePreservesExpandedStateAndChildInstancesOnRefresh()
    {
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), new StubPrivilege(false), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var fakeNet = new MutableNetworkManagerService
        {
            Connections =
            [
                new("TCP", "192.168.1.10", 51001, "142.250.185.78", 443, "SYN_SENT", 1001, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe"),
                new("TCP", "192.168.1.10", 51002, "142.250.185.79", 443, "ESTABLISHED", 1001, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe")
            ]
        };

        using var vm = new NetworkViewModel(fakeNet, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        var initialGroup = Assert.Single(vm.ConnectionGroups);
        initialGroup.ToggleExpandedCommand.Execute(null);
        Assert.True(initialGroup.IsExpanded);
        var firstChild = initialGroup.Connections[0];

        // Refresh updates child state and adds a third connection
        fakeNet.Connections =
        [
            new("TCP", "192.168.1.10", 51001, "142.250.185.78", 443, "ESTABLISHED", 1001, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe"),
            new("TCP", "192.168.1.10", 51002, "142.250.185.79", 443, "ESTABLISHED", 1001, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe"),
            new("TCP", "192.168.1.10", 51003, "142.250.185.80", 443, "ESTABLISHED", 1002, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe")
        ];

        await vm.RefreshAsync();

        var refreshedGroup = Assert.Single(vm.ConnectionGroups);
        Assert.Same(initialGroup, refreshedGroup);
        Assert.True(refreshedGroup.IsExpanded);
        Assert.Equal(3, refreshedGroup.ConnectionCount);
        Assert.Equal(2, refreshedGroup.PidCount);
        Assert.Same(firstChild, refreshedGroup.Connections[0]);
        Assert.Equal("ESTABLISHED", firstChild.State);
        Assert.True(firstChild.IsEstablished);
    }

    [Fact]
    public async Task ConnectionGroups_QuickBlockCommandBlocksApplicationByExecutablePath()
    {
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), new StubPrivilege(true), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var fakeNet = new MutableNetworkManagerService
        {
            Connections =
            [
                new("TCP", "192.168.1.10", 51001, "142.250.185.78", 443, "ESTABLISHED", 1001, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe")
            ]
        };

        using var vm = new NetworkViewModel(fakeNet, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        var group = Assert.Single(vm.ConnectionGroups);
        Assert.True(group.HasExecutablePath);
        Assert.True(group.BlockAppCommand.CanExecute(null));

        var asyncCmd = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(group.BlockAppCommand);
        await asyncCmd.ExecuteAsync(null);

        var blocked = Assert.Single(fakeNet.BlockedApps);
        Assert.Equal("chrome", blocked.AppName);
        Assert.Equal(@"C:\Program Files\Google\Chrome\chrome.exe", blocked.ExecutablePath);
    }
}
