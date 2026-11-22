using Argus.App.Services;
using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Argus.Core.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class Phase1StartupNetworkViewModelTests
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

    private sealed class RecordingPrivilegeService(bool isAdmin) : IPrivilegeService
    {
        public bool IsAdministrator { get; } = isAdmin;
        public string[]? LastArgs { get; private set; }

        public bool TryRelaunchElevated(string[]? arguments = null)
        {
            LastArgs = arguments;
            return true;
        }
    }

    private sealed class FakeStartupAndServicesManager : IStartupAndServicesManager
    {
        public bool LastIncludeSystemTasks { get; private set; }

        public Task<IReadOnlyList<StartupEntryItem>> GetStartupEntriesAsync(
            bool includeSystemTasks = false,
            CancellationToken cancellationToken = default)
        {
            LastIncludeSystemTasks = includeSystemTasks;
            var list = new List<StartupEntryItem>
            {
                new("HKCU:Argus", "ARGUS", "C:\\Program Files\\ARGUS\\Argus.exe", StartupSourceKind.RegistryCurrentUserRun, @"HKCU\...\Run", true, false, false, "Поточний користувач"),
                new("HKLM:Audio", "RealtekAudio", "C:\\Program Files\\Realtek\\audio.exe", StartupSourceKind.RegistryLocalMachineRun, @"HKLM\...\Run", false, true, false, "Усі користувачі (HKLM)")
            };
            if (includeSystemTasks)
            {
                list.Add(new("Task:\\Microsoft\\Sys", "SysTask", "svchost.exe", StartupSourceKind.TaskScheduler, @"\Microsoft\Sys", true, true, true, "Microsoft"));
            }

            return Task.FromResult<IReadOnlyList<StartupEntryItem>>(list);
        }

        public Task<SystemActionResult> SetStartupEntryEnabledAsync(StartupEntryItem entry, bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));

        public Task<IReadOnlyList<ManagedServiceItem>> GetManagedServicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedServiceItem>>(
            [
                new("RpcSs", "Remote Procedure Call (RPC)", "Працює", ServiceStartModeOption.Automatic, "Автоматично", false, true, "C:\\Windows\\system32\\svchost.exe", "Core RPC"),
                new("Spooler", "Print Spooler", "Зупинено", ServiceStartModeOption.Manual, "Вручну", false, false, "C:\\Windows\\system32\\spoolsv.exe", "Printing")
            ]);

        public Task<SystemActionResult> ControlServiceAsync(string serviceName, ServiceControlAction action, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));

        public Task<SystemActionResult> SetServiceStartModeAsync(string serviceName, ServiceStartModeOption startMode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));
    }

    private sealed class FakeNetworkManagerService : INetworkManagerService
    {
        public FirewallRuleCreateRequest? LastCreatedRule { get; private set; }

        public Task<IReadOnlyList<ActiveConnectionItem>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActiveConnectionItem>>(
            [
                new("TCP", "192.168.1.10", 51234, "142.250.185.78", 443, "ESTABLISHED", 1234, "chrome", "C:\\Program Files\\Google\\Chrome\\chrome.exe"),
                new("TCP", "127.0.0.1", 8080, "127.0.0.1", 55443, "ESTABLISHED", 4321, "localdev", "C:\\Dev\\localdev.exe")
            ]);

        public Task<IReadOnlyList<FirewallRuleItem>> GetFirewallRulesAsync(bool argusOnly = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirewallRuleItem>>(
            [
                new("ARGUS_Block_Out_Demo", "Blocked by ARGUS", FirewallRuleDirection.Outbound, FirewallRuleAction.Block, true, "C:\\Demo\\demo.exe", "ANY", "*", "*", "Усі профілі", true),
                new("Windows Core Rule", "System rule", FirewallRuleDirection.Inbound, FirewallRuleAction.Allow, true, null, "TCP", "80", "*", "Приватна", false)
            ]);

        public Task<SystemActionResult> BlockApplicationAsync(string appName, string executablePath, FirewallRuleDirection direction = FirewallRuleDirection.Outbound, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Blocked"));

        public Task<SystemActionResult> CreateFirewallRuleAsync(FirewallRuleCreateRequest request, CancellationToken cancellationToken = default)
        {
            LastCreatedRule = request;
            return Task.FromResult(new SystemActionResult(true, false, $"Created {request.Name}"));
        }

        public Task<SystemActionResult> SetFirewallRuleEnabledAsync(string ruleName, bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Toggled"));

        public Task<SystemActionResult> DeleteArgusFirewallRuleAsync(string ruleName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "Deleted"));
    }

    [Fact]
    public async Task StartupAndServicesViewModel_LoadsCountsSyncsExpertAndRelaunchesOnSection()
    {
        var priv = new RecordingPrivilegeService(isAdmin: false);
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), priv, new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var fakeMgr = new FakeStartupAndServicesManager();
        using var vm = new StartupAndServicesViewModel(fakeMgr, uiMode: uiMode, autoLoadOnInit: false);

        await vm.RefreshAsync();

        Assert.Equal(2, vm.StartupCount);
        Assert.Equal(2, vm.ServiceCount);
        Assert.Equal(1, vm.ProtectedServiceCount);
        Assert.False(fakeMgr.LastIncludeSystemTasks);
        Assert.True(vm.NeedsElevation);

        vm.RequestElevationCommand.Execute(null);
        Assert.NotNull(priv.LastArgs);
        Assert.Equal(["--section", "startupandservices"], priv.LastArgs!);
    }

    [Fact]
    public async Task NetworkViewModel_HidesLoopbackInBasicModeShowsInExpertAndEnforcesArgusPrefix()
    {
        var priv = new RecordingPrivilegeService(isAdmin: false);
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), priv, new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var fakeNet = new FakeNetworkManagerService();
        using var vm = new NetworkViewModel(fakeNet, uiMode: uiMode, autoLoadOnInit: false);

        await vm.RefreshAsync();

        // Basic mode excludes loopback (127.0.0.1)
        Assert.Single(vm.Connections);
        Assert.Equal("chrome", vm.Connections[0].ProcessName);

        // Default firewall filter shows only ARGUS_ rules
        Assert.Single(vm.FirewallRules);
        Assert.True(vm.FirewallRules[0].IsArgusManaged);

        // Expert mode exposes loopback sockets
        vm.IsExpertMode = true;
        Assert.Equal(2, vm.Connections.Count);

        // Creating a rule enforces ARGUS_ prefix
        vm.NewRuleName = "Block_Telemetry";
        vm.NewRulePorts = "443";
        await vm.CreateNewRuleAsync();

        Assert.NotNull(fakeNet.LastCreatedRule);
        Assert.Equal("ARGUS_Block_Telemetry", fakeNet.LastCreatedRule!.Name);
        Assert.Equal("TCP", fakeNet.LastCreatedRule.Protocol);

        vm.RequestElevationCommand.Execute(null);
        Assert.NotNull(priv.LastArgs);
        Assert.Equal(["--section", "network"], priv.LastArgs!);
    }
}
