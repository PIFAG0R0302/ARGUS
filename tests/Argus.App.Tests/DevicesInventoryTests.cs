using Argus.App.ViewModels;
using Argus.Core.Contracts.Devices;
using Argus.Core.Events;
using Argus.Core.Journal;
using Argus.Devices.Monitoring;
using Argus.Devices.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class DevicesInventoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusDevicesTests_{Guid.NewGuid():N}");

    public DevicesInventoryTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void SmbiosAndSetupApiDecoders_DecodeMemoryTypesManufacturersDatesAndBusTypes()
    {
        Assert.Equal("DDR4", WindowsDeviceProvider.DecodeSmbiosMemoryType(26, 2666));
        Assert.Equal("DDR5", WindowsDeviceProvider.DecodeSmbiosMemoryType(34, 5600));
        Assert.Equal("Patriot Memory", WindowsDeviceProvider.DecodeRamManufacturer("8502"));
        Assert.Equal("Samsung", WindowsDeviceProvider.DecodeRamManufacturer("80CE"));
        Assert.Equal("2021-11-05", WindowsDeviceProvider.FormatWmiDate("20211105000000.000000+000"));

        Assert.Equal("PCIe", WindowsDeviceProvider.ClassifyBusType(@"PCI\VEN_10DE&DEV_2184", "PCI", "Display"));
        Assert.Equal("USB", WindowsDeviceProvider.ClassifyBusType(@"USB\VID_046D&PID_C52B", "USB", "USB"));
        Assert.Equal("Диски", WindowsDeviceProvider.ClassifyBusType(@"SCSI\DISK&VEN_NVME", "SCSI", "DiskDrive"));
        Assert.Equal("Аудіо", WindowsDeviceProvider.ClassifyBusType(@"HDAUDIO\FUNC_01", "HDAUDIO", "Media"));
    }

    [Fact]
    public void WindowsDeviceChangeMonitor_DebouncesBurstsPublishesEventsAndRecordsJournal()
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "pnp-logs", "journal.jsonl"));

        using var monitor = new WindowsDeviceChangeMonitor(
            bus,
            journal,
            NullLogger<WindowsDeviceChangeMonitor>.Instance);

        var received = new List<DeviceTopologyChangedEvent>();
        using var sub = bus.Subscribe<DeviceTopologyChangedEvent>(e => received.Add(e));

        monitor.RaiseTopologyChange(DeviceChangeKind.Arrived, @"USB\VID_0951&PID_1666");
        monitor.RaiseTopologyChange(DeviceChangeKind.Arrived, @"USB\VID_0951&PID_1666"); // within 350ms debounce

        Assert.Single(received);
        Assert.Equal(DeviceChangeKind.Arrived, received[0].Kind);

        monitor.RaiseTopologyChange(DeviceChangeKind.Removed, @"USB\VID_0951&PID_1666");
        Assert.Equal(2, received.Count);
        Assert.Equal(DeviceChangeKind.Removed, received[1].Kind);
        Assert.Contains(journal.GetRecent(10), static j => j.Action == "DeviceTopologyChanged");
    }

    [Fact]
    public async Task DevicesViewModel_LoadsInventoryFiltersByBusAndSearchAndReactsToTopologyChange()
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        var provider = new StubDeviceProvider();

        using var vm = new DevicesViewModel(provider, bus, autoLoadOnInit: false);
        await vm.RefreshDevicesAsync();

        Assert.Contains("i7-9700K", vm.CpuTitle, StringComparison.Ordinal);
        Assert.Contains("Z390", vm.BoardTitle, StringComparison.Ordinal);
        Assert.Equal(2, vm.RamSlots.Count);
        Assert.Contains("GTX 1660", vm.PrimaryGpuTitle, StringComparison.Ordinal);
        Assert.Equal(4, vm.FilteredDevices.Count);

        vm.FilterPcieCommand.Execute(null);
        Assert.True(vm.IsFilterPcie);
        Assert.Equal(2, vm.FilteredDevices.Count);
        Assert.Contains(vm.FilteredDevices, static d => d.FriendlyName == "NVIDIA GeForce GTX 1660");

        vm.FilterUsbCommand.Execute(null);
        Assert.True(vm.IsFilterUsb);
        Assert.Single(vm.FilteredDevices);
        Assert.Contains("USB", vm.FilteredDevices[0].FriendlyName, StringComparison.Ordinal);

        vm.FilterStorageCommand.Execute(null);
        Assert.True(vm.IsFilterStorage);
        Assert.Single(vm.FilteredDevices);
        Assert.Contains("SPCC", vm.FilteredDevices[0].FriendlyName, StringComparison.Ordinal);

        vm.FilterProblemsCommand.Execute(null);
        Assert.True(vm.IsFilterProblems);
        Assert.Single(vm.FilteredDevices);
        Assert.True(vm.FilteredDevices[0].IsProblem);

        vm.FilterAllCommand.Execute(null);
        vm.SearchQuery = "VEN_10DE";
        Assert.Single(vm.FilteredDevices);
        Assert.Equal("NVIDIA GeForce GTX 1660", vm.FilteredDevices[0].FriendlyName);

        vm.SearchQuery = string.Empty;
        Assert.Equal(4, vm.FilteredDevices.Count);

        int callsBefore = provider.GetDevicesCalls;
        bus.Publish(new DeviceTopologyChangedEvent(DeviceChangeKind.Arrived, @"USB\VID_1111", DateTimeOffset.UtcNow));

        // Wait briefly for async topology refresh to complete
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (provider.GetDevicesCalls <= callsBefore && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(provider.GetDevicesCalls > callsBefore);
        Assert.Contains("Підключено пристрій", vm.PnpMonitorStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealWindowsDeviceProvider_CollectsLiveHardwareInventoryAndSetupApiDeviceTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var provider = new WindowsDeviceProvider();

        var inventory = await provider.GetInventoryAsync(ct);
        var devices = await provider.GetDevicesAsync(ct);

        Assert.False(string.IsNullOrWhiteSpace(inventory.Cpu.Name));
        Assert.True(inventory.Cpu.LogicalProcessors >= 1);
        Assert.NotEmpty(inventory.RamModules);
        Assert.False(string.IsNullOrWhiteSpace(inventory.Motherboard.Product));
        Assert.NotEmpty(inventory.Gpus);
        Assert.True(devices.Count >= 20);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private sealed class StubDeviceProvider : IDeviceProvider
    {
        public int GetDevicesCalls { get; private set; }

        public Task<HardwareInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
        {
            var inv = new HardwareInventory(
                DateTimeOffset.UtcNow,
                new OsVersionInfo("Windows 11 Pro", "24H2", "26300", "X64", IsUefiFirmware: true),
                new CpuSpec("Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", "Intel Corporation", "X64", "U3E1", 8, 8, 3600, 2 * 1024 * 1024, 12 * 1024 * 1024),
                [
                    new RamModuleSpec("ChannelA-DIMM0", "Patriot Memory", "3200 C16 Series", 16L * 1024 * 1024 * 1024, 2666, "DDR4"),
                    new RamModuleSpec("ChannelB-DIMM0", "Patriot Memory", "3200 C16 Series", 16L * 1024 * 1024 * 1024, 2666, "DDR4")
                ],
                new MotherboardSpec("Gigabyte Technology Co., Ltd.", "Z390 M-CF", "American Megatrends Inc.", "F6", "2021-11-05"),
                [new GpuSpec("NVIDIA GeForce GTX 1660", "NVIDIA", "32.0.16.1088", 6L * 1024 * 1024 * 1024, @"PCI\VEN_10DE&DEV_2184")],
                [new NetworkAdapterSpec("Intel Ethernet I219-V", "AA:BB:CC:DD:EE:FF", 1_000_000_000, true, "Ethernet")]);

            return Task.FromResult(inv);
        }

        public Task<IReadOnlyList<DeviceNode>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            GetDevicesCalls++;
            IReadOnlyList<DeviceNode> nodes =
            [
                new(@"PCI\VEN_10DE&DEV_2184", "NVIDIA GeForce GTX 1660", "Відеоадаптери", "PCIe", "NVIDIA", "32.0.16.1088", "10-15-2024", DeviceStatus.Ok, 0, true, null),
                new(@"USB\VID_046D&PID_C52B", "USB Composite Receiver", "Контролери та пристрої USB", "USB", "Logitech", "10.0.26100.1", null, DeviceStatus.Ok, 0, true, null),
                new(@"SCSI\DISK&VEN_SPCC", "SPCC Solid State Disk", "Дискові накопичувачі", "Диски", "Стандартний драйвер Windows", "10.0.26100.1", null, DeviceStatus.Ok, 0, true, null),
                new(@"PCI\VEN_8086&DEV_A36D", "Невідомий контролер PCI", "Системні компоненти", "PCIe", "Невідомо", "—", null, DeviceStatus.Warning, 28, true, null)
            ];
            return Task.FromResult(nodes);
        }
    }
}
