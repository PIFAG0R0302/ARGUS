using Argus.App.ViewModels;
using Argus.Core.Contracts.Devices;
using Argus.Devices.Providers;
using Xunit;

namespace Argus.App.Tests;

public sealed class DevicesDriverSearchTests
{
    private static readonly DeviceNode SampleGpuNode = new(
        InstanceId: @"PCI\VEN_10DE&DEV_2184&SUBSYS_00000000&REV_A1\4&123456&0&0008",
        FriendlyName: "NVIDIA GeForce GTX 1660",
        Category: "Відеоадаптери",
        BusType: "PCIe",
        Manufacturer: "NVIDIA",
        DriverVersion: "32.0.16.1088",
        DriverDate: "10-15-2024",
        Status: DeviceStatus.Ok,
        ProblemCode: 0,
        IsPresent: true,
        ParentInstanceId: null);

    [Fact]
    public async Task DevicesViewModel_SearchDeviceDriverCommand_InvokesProviderAndUpdatesFeedback()
    {
        var provider = new RecordingDriverSearchProvider(
            new DriverSearchResult(
                Succeeded: true,
                Outcome: DriverSearchOutcome.WizardLaunched,
                Message: "Відкрито вікно властивостей і оновлення драйвера Windows для «NVIDIA GeForce GTX 1660»."));

        using var vm = new DevicesViewModel(provider, autoLoadOnInit: false);
        var row = new DeviceNodeRowItem(SampleGpuNode);

        await vm.SearchDeviceDriverCommand.ExecuteAsync(row);

        Assert.Equal(1, provider.SearchCalls);
        Assert.Equal(SampleGpuNode.InstanceId, provider.LastInstanceId);
        Assert.Equal(SampleGpuNode.FriendlyName, provider.LastFriendlyName);
        Assert.True(vm.HasExpertActionFeedback);
        Assert.Contains("NVIDIA GeForce GTX 1660", vm.ExpertActionFeedback, StringComparison.Ordinal);
        Assert.False(vm.IsSearchingDriver);
        Assert.Equal(0, provider.GetDevicesCalls);
    }

    [Fact]
    public async Task DevicesViewModel_SearchDeviceDriverCommand_RefreshesDevicesWhenDriverUpdated()
    {
        var provider = new RecordingDriverSearchProvider(
            new DriverSearchResult(
                Succeeded: true,
                Outcome: DriverSearchOutcome.Updated,
                Message: "Пошук та оновлення драйвера для «NVIDIA GeForce GTX 1660» завершено."));

        using var vm = new DevicesViewModel(provider, autoLoadOnInit: false);
        var row = new DeviceNodeRowItem(SampleGpuNode);

        await vm.SearchDeviceDriverCommand.ExecuteAsync(row);

        Assert.Equal(1, provider.SearchCalls);
        Assert.Equal(1, provider.GetDevicesCalls);
        Assert.True(vm.HasExpertActionFeedback);
        Assert.Contains("оновлення драйвера", vm.ExpertActionFeedback, StringComparison.Ordinal);
        Assert.False(vm.IsSearchingDriver);
    }

    [Fact]
    public async Task DevicesViewModel_SearchDeviceDriverCommand_UsesCustomDriverSearchHandlerWhenSet()
    {
        var provider = new RecordingDriverSearchProvider(
            new DriverSearchResult(true, DriverSearchOutcome.WizardLaunched, "provider"));
        using var vm = new DevicesViewModel(provider, autoLoadOnInit: false);

        string? capturedId = null;
        vm.DriverSearchHandler = (item, _) =>
        {
            capturedId = item.InstanceId;
            return Task.FromResult(new DriverSearchResult(
                Succeeded: true,
                Outcome: DriverSearchOutcome.FallbackWindowsUpdateOpened,
                Message: "Відкрито «Необов'язкові оновлення» Windows Update."));
        };

        var row = new DeviceNodeRowItem(SampleGpuNode);
        await vm.SearchDeviceDriverCommand.ExecuteAsync(row);

        Assert.Equal(0, provider.SearchCalls);
        Assert.Equal(SampleGpuNode.InstanceId, capturedId);
        Assert.True(vm.HasExpertActionFeedback);
        Assert.Contains("Windows Update", vm.ExpertActionFeedback, StringComparison.Ordinal);
        Assert.False(vm.IsSearchingDriver);
    }

    [Fact]
    public async Task DevicesViewModel_SearchDeviceDriverCommand_HandlesProviderExceptionGracefully()
    {
        var provider = new ThrowingDriverSearchProvider();
        using var vm = new DevicesViewModel(provider, autoLoadOnInit: false);
        var row = new DeviceNodeRowItem(SampleGpuNode);

        await vm.SearchDeviceDriverCommand.ExecuteAsync(row);

        Assert.True(vm.HasExpertActionFeedback);
        Assert.Contains("Помилка пошуку драйверів", vm.ExpertActionFeedback, StringComparison.Ordinal);
        Assert.Contains("Access denied", vm.ExpertActionFeedback, StringComparison.Ordinal);
        Assert.False(vm.IsSearchingDriver);
    }

    [Fact]
    public async Task DevicesViewModel_SearchDeviceDriverCommand_IgnoresNullOrEmptyInstanceId()
    {
        var provider = new RecordingDriverSearchProvider(
            new DriverSearchResult(true, DriverSearchOutcome.WizardLaunched, "ok"));
        using var vm = new DevicesViewModel(provider, autoLoadOnInit: false);

        await vm.SearchDeviceDriverCommand.ExecuteAsync(null);
        await vm.SearchDeviceDriverCommand.ExecuteAsync(new DeviceNodeRowItem(SampleGpuNode with { InstanceId = "   " }));

        Assert.Equal(0, provider.SearchCalls);
        Assert.False(vm.HasExpertActionFeedback);
        Assert.False(vm.IsSearchingDriver);
    }

    [Fact]
    public void DevicesViewModel_CopyInstanceIdCommand_RemainsAvailableForBackwardCompatibility()
    {
        using var vm = new DevicesViewModel(autoLoadOnInit: false);
        string? copied = null;
        vm.CopyToClipboardHandler = text => copied = text;

        vm.CopyInstanceIdCommand.Execute(SampleGpuNode.InstanceId);

        Assert.Equal(SampleGpuNode.InstanceId, copied);
        Assert.True(vm.HasExpertActionFeedback);
        Assert.Contains(SampleGpuNode.InstanceId, vm.ExpertActionFeedback, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsDeviceProvider_ExecuteDriverSearch_ElevatedSuccessAndRebootFlag()
    {
        bool pnpCalled = false;
        bool wuCalled = false;

        var result = WindowsDeviceProvider.ExecuteDriverSearch(
            SampleGpuNode.InstanceId,
            SampleGpuNode.FriendlyName,
            isElevated: static () => true,
            invokeDiInstallDevice: static _ => new WindowsDeviceProvider.NativeDriverInstallAttempt(
                WindowsDeviceProvider.NativeInstallStatus.Installed,
                RebootRequired: true,
                LastWin32Error: 0),
            launchElevatedDeviceProperties: static _ => false,
            runPnpUtilScanDevices: () => pnpCalled = true,
            openWindowsUpdateOptionalUpdates: () =>
            {
                wuCalled = true;
                return true;
            });

        Assert.True(result.Succeeded);
        Assert.Equal(DriverSearchOutcome.Updated, result.Outcome);
        Assert.True(result.RebootRequired);
        Assert.Contains("перезавантаження", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(pnpCalled);
        Assert.False(wuCalled);
    }

    [Fact]
    public void WindowsDeviceProvider_ExecuteDriverSearch_ElevatedCancelledByUser_DoesNotTriggerFallback()
    {
        bool pnpCalled = false;
        bool wuCalled = false;

        var result = WindowsDeviceProvider.ExecuteDriverSearch(
            SampleGpuNode.InstanceId,
            SampleGpuNode.FriendlyName,
            isElevated: static () => true,
            invokeDiInstallDevice: static _ => new WindowsDeviceProvider.NativeDriverInstallAttempt(
                WindowsDeviceProvider.NativeInstallStatus.Cancelled,
                RebootRequired: false,
                LastWin32Error: 1223),
            launchElevatedDeviceProperties: static _ => false,
            runPnpUtilScanDevices: () => pnpCalled = true,
            openWindowsUpdateOptionalUpdates: () =>
            {
                wuCalled = true;
                return true;
            });

        Assert.True(result.Succeeded);
        Assert.Equal(DriverSearchOutcome.WizardLaunched, result.Outcome);
        Assert.Contains("закрито користувачем", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(pnpCalled);
        Assert.False(wuCalled);
    }

    [Fact]
    public void WindowsDeviceProvider_ExecuteDriverSearch_ElevatedFailure_FallsBackToPnpUtilAndWindowsUpdate()
    {
        bool pnpCalled = false;
        bool wuCalled = false;

        var result = WindowsDeviceProvider.ExecuteDriverSearch(
            SampleGpuNode.InstanceId,
            SampleGpuNode.FriendlyName,
            isElevated: static () => true,
            invokeDiInstallDevice: static _ => new WindowsDeviceProvider.NativeDriverInstallAttempt(
                WindowsDeviceProvider.NativeInstallStatus.Failed,
                RebootRequired: false,
                LastWin32Error: 5),
            launchElevatedDeviceProperties: static _ => false,
            runPnpUtilScanDevices: () => pnpCalled = true,
            openWindowsUpdateOptionalUpdates: () =>
            {
                wuCalled = true;
                return true;
            });

        Assert.True(result.Succeeded);
        Assert.Equal(DriverSearchOutcome.FallbackWindowsUpdateOpened, result.Outcome);
        Assert.True(pnpCalled);
        Assert.True(wuCalled);
        Assert.Contains("Windows Update", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsDeviceProvider_ExecuteDriverSearch_NonElevated_LaunchesPropertiesOrFallsBack()
    {
        bool pnpCalled = false;
        bool wuCalled = false;

        var launched = WindowsDeviceProvider.ExecuteDriverSearch(
            SampleGpuNode.InstanceId,
            SampleGpuNode.FriendlyName,
            isElevated: static () => false,
            invokeDiInstallDevice: static _ => throw new InvalidOperationException("Should not call DiInstallDevice when not elevated."),
            launchElevatedDeviceProperties: static _ => true,
            runPnpUtilScanDevices: () => pnpCalled = true,
            openWindowsUpdateOptionalUpdates: () =>
            {
                wuCalled = true;
                return true;
            });

        Assert.True(launched.Succeeded);
        Assert.Equal(DriverSearchOutcome.WizardLaunched, launched.Outcome);
        Assert.False(pnpCalled);
        Assert.False(wuCalled);

        var fallback = WindowsDeviceProvider.ExecuteDriverSearch(
            SampleGpuNode.InstanceId,
            SampleGpuNode.FriendlyName,
            isElevated: static () => false,
            invokeDiInstallDevice: static _ => throw new InvalidOperationException("Should not call DiInstallDevice when not elevated."),
            launchElevatedDeviceProperties: static _ => false,
            runPnpUtilScanDevices: () => pnpCalled = true,
            openWindowsUpdateOptionalUpdates: () =>
            {
                wuCalled = true;
                return true;
            });

        Assert.True(fallback.Succeeded);
        Assert.Equal(DriverSearchOutcome.FallbackWindowsUpdateOpened, fallback.Outcome);
        Assert.True(pnpCalled);
        Assert.True(wuCalled);

        var failed = WindowsDeviceProvider.ExecuteDriverSearch(
            SampleGpuNode.InstanceId,
            SampleGpuNode.FriendlyName,
            isElevated: static () => false,
            invokeDiInstallDevice: static _ => throw new InvalidOperationException("Should not call DiInstallDevice when not elevated."),
            launchElevatedDeviceProperties: static _ => false,
            runPnpUtilScanDevices: static () => { },
            openWindowsUpdateOptionalUpdates: static () => false);

        Assert.False(failed.Succeeded);
        Assert.Equal(DriverSearchOutcome.Failed, failed.Outcome);
    }

    [Fact]
    public async Task DeviceContracts_DefaultInterfaceMethod_ReturnsUnsupported()
    {
        IDeviceProvider minimalProvider = new MinimalDeviceProvider();
        var result = await minimalProvider.SearchAndUpdateDriverAsync(
            SampleGpuNode.InstanceId,
            SampleGpuNode.FriendlyName,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DriverSearchOutcome.Unsupported, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    private sealed class RecordingDriverSearchProvider(DriverSearchResult resultToReturn) : IDeviceProvider
    {
        public int SearchCalls { get; private set; }
        public int GetDevicesCalls { get; private set; }
        public string? LastInstanceId { get; private set; }
        public string? LastFriendlyName { get; private set; }

        public Task<HardwareInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new HardwareInventory(
                DateTimeOffset.UtcNow,
                new OsVersionInfo("Windows 11 Pro", "24H2", "26100", "X64", true),
                new CpuSpec("Test CPU", "Intel", "X64", "LGA", 8, 16, 4000, 0, 0),
                [],
                new MotherboardSpec("Vendor", "Board", "BIOS", "1.0", "2025-01-01"),
                [],
                []));
        }

        public Task<IReadOnlyList<DeviceNode>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            GetDevicesCalls++;
            IReadOnlyList<DeviceNode> nodes = [SampleGpuNode];
            return Task.FromResult(nodes);
        }

        public Task<DriverSearchResult> SearchAndUpdateDriverAsync(
            string deviceInstanceId,
            string? friendlyName = null,
            CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            LastInstanceId = deviceInstanceId;
            LastFriendlyName = friendlyName;
            return Task.FromResult(resultToReturn);
        }
    }

    private sealed class ThrowingDriverSearchProvider : IDeviceProvider
    {
        public Task<HardwareInventory> GetInventoryAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeviceNode>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DriverSearchResult> SearchAndUpdateDriverAsync(
            string deviceInstanceId,
            string? friendlyName = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<DriverSearchResult>(new InvalidOperationException("Access denied"));
    }

    private sealed class MinimalDeviceProvider : IDeviceProvider
    {
        public Task<HardwareInventory> GetInventoryAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DeviceNode>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
