using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Devices;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class UxDevicesCleanupUpdateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusUxDevCleanupTests_{Guid.NewGuid():N}");

    public UxDevicesCleanupUpdateTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task DevicesViewModel_GroupsDuplicateDevicesByNormalizedNameAndStartsCollapsed()
    {
        var provider = new MultiInterfaceDeviceProvider();
        using var vm = new DevicesViewModel(provider, autoLoadOnInit: false);

        await vm.RefreshDevicesAsync();

        // 6 raw nodes collapse into 3 groups (3 keyboards + 2 audio + 1 GPU)
        Assert.Equal(6, vm.FilteredDevices.Count);
        Assert.Equal(3, vm.DeviceGroups.Count);

        var keyboardGroup = Assert.Single(vm.DeviceGroups, static g => g.DisplayName == "HID Keyboard Device");
        Assert.Equal(3, keyboardGroup.ItemCount);
        Assert.True(keyboardGroup.HasMultipleItems);
        Assert.Equal("3 записи", keyboardGroup.CountBadgeText);
        Assert.False(keyboardGroup.IsExpanded);
        Assert.Equal("▶", keyboardGroup.ExpandChevron);
        Assert.True(keyboardGroup.IsAllOk);
        Assert.False(keyboardGroup.HasProblem);
        Assert.Equal("OK", keyboardGroup.StatusLabel);
        Assert.Equal(3, keyboardGroup.Items.Count);

        var audioGroup = Assert.Single(vm.DeviceGroups, static g => g.DisplayName == "USB Audio Device");
        Assert.Equal(2, audioGroup.ItemCount);
        Assert.True(audioGroup.HasMultipleItems);
        Assert.Equal("2 записи", audioGroup.CountBadgeText);
        Assert.False(audioGroup.IsExpanded);
        Assert.True(audioGroup.HasProblem);
        Assert.False(audioGroup.IsAllOk);
        Assert.Equal("Код 28", audioGroup.StatusLabel);
        Assert.Equal(2, audioGroup.Items.Count);

        var gpuGroup = Assert.Single(vm.DeviceGroups, static g => g.DisplayName == "NVIDIA GeForce GTX 1660");
        Assert.Equal(1, gpuGroup.ItemCount);
        Assert.False(gpuGroup.HasMultipleItems);
        Assert.Equal(string.Empty, gpuGroup.CountBadgeText);
        Assert.Equal(string.Empty, gpuGroup.ExpandChevron);
        Assert.False(gpuGroup.IsExpanded);
        Assert.Single(gpuGroup.Items);
    }

    [Fact]
    public async Task DevicesViewModel_ToggleExpandedPreservesStateAcrossRefreshes()
    {
        var provider = new MultiInterfaceDeviceProvider();
        using var vm = new DevicesViewModel(provider, autoLoadOnInit: false);

        await vm.RefreshDevicesAsync();

        var keyboardGroup = Assert.Single(vm.DeviceGroups, static g => g.DisplayName == "HID Keyboard Device");
        Assert.False(keyboardGroup.IsExpanded);

        keyboardGroup.ToggleExpandedCommand.Execute(null);
        Assert.True(keyboardGroup.IsExpanded);
        Assert.Equal("▼", keyboardGroup.ExpandChevron);

        // Refreshing via CollectionSyncHelper.SyncByKey must keep the same row instance and expanded state
        await vm.RefreshDevicesAsync();

        var refreshedGroup = Assert.Single(vm.DeviceGroups, static g => g.DisplayName == "HID Keyboard Device");
        Assert.Same(keyboardGroup, refreshedGroup);
        Assert.True(refreshedGroup.IsExpanded);
        Assert.Equal("▼", refreshedGroup.ExpandChevron);

        refreshedGroup.ToggleExpandedCommand.Execute(null);
        Assert.False(refreshedGroup.IsExpanded);
        Assert.Equal("▶", refreshedGroup.ExpandChevron);
    }

    [Fact]
    public async Task DevicesViewModel_BusFiltersApplyToDeviceGroupsAndDefaultStatusOmitsCfgMgr32()
    {
        var provider = new MultiInterfaceDeviceProvider();
        using var vm = new DevicesViewModel(provider, autoLoadOnInit: false);

        Assert.DoesNotContain("CfgMgr32", vm.PnpMonitorStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CFG MGR 32", vm.PnpMonitorStatusText, StringComparison.OrdinalIgnoreCase);

        await vm.RefreshDevicesAsync();

        vm.FilterUsbCommand.Execute(null);
        Assert.Single(vm.DeviceGroups);
        Assert.Equal("HID Keyboard Device", vm.DeviceGroups[0].DisplayName);
        Assert.Equal(3, vm.DeviceGroups[0].ItemCount);

        vm.FilterNetAudioCommand.Execute(null);
        Assert.Single(vm.DeviceGroups);
        Assert.Equal("USB Audio Device", vm.DeviceGroups[0].DisplayName);
        Assert.Equal(2, vm.DeviceGroups[0].ItemCount);

        vm.FilterProblemsCommand.Execute(null);
        Assert.Single(vm.DeviceGroups);
        Assert.Equal("USB Audio Device", vm.DeviceGroups[0].DisplayName);
        Assert.Equal(1, vm.DeviceGroups[0].ItemCount);
        Assert.True(vm.DeviceGroups[0].HasProblem);

        vm.FilterAllCommand.Execute(null);
        Assert.Equal(3, vm.DeviceGroups.Count);
    }

    [Fact]
    public async Task CleanupViewModel_RetainsBackendExclusionsAfterUiPanelRemoval()
    {
        string workDir = Path.Combine(_tempDir, "cleanup");
        Directory.CreateDirectory(workDir);

        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var store = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(workDir, "settings.json"));
        var stubCleaner = new RecordingCleanerService();

        using var vm = new CleanupViewModel(
            store,
            cleanerService: stubCleaner,
            eventBus: bus,
            rulesDirectory: Path.Combine(workDir, "rules"),
            autoLoadOnInit: false);

        string excludedPath = Path.Combine(workDir, "keep-me");
        vm.NewExclusionPath = excludedPath;
        await vm.AddExclusionAsync();

        Assert.Contains(excludedPath, vm.Exclusions);

        await vm.AnalyzeAsync();
        Assert.Contains(excludedPath, stubCleaner.LastAnalyzeExclusions);

        await vm.RemoveExclusionAsync(excludedPath);
        Assert.DoesNotContain(excludedPath, vm.Exclusions);
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

    private sealed class MultiInterfaceDeviceProvider : IDeviceProvider
    {
        public Task<HardwareInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
        {
            var inv = new HardwareInventory(
                DateTimeOffset.UtcNow,
                new OsVersionInfo("Windows 11 Pro", "24H2", "26300", "X64", IsUefiFirmware: true),
                new CpuSpec("Intel(R) Core(TM) i7-9700K CPU @ 3.60GHz", "Intel Corporation", "X64", "U3E1", 8, 8, 3600, 2 * 1024 * 1024, 12 * 1024 * 1024),
                [],
                new MotherboardSpec("Gigabyte", "Z390", "AMI", "F6", "2021-11-05"),
                [new GpuSpec("NVIDIA GeForce GTX 1660", "NVIDIA", "32.0.16.1088", 6L * 1024 * 1024 * 1024, @"PCI\VEN_10DE&DEV_2184")],
                []);

            return Task.FromResult(inv);
        }

        public Task<IReadOnlyList<DeviceNode>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DeviceNode> nodes =
            [
                new(@"HID\VID_046D&MI_00\1", "HID Keyboard Device", "Клавіатури", "HID", "Logitech", "10.0.26100.1", "01-01-2025", DeviceStatus.Ok, 0, true, null),
                new(@"HID\VID_046D&MI_01\2", "hid  keyboard device", "Клавіатури", "HID", "Logitech", "10.0.26100.1", "01-01-2025", DeviceStatus.Ok, 0, true, null),
                new(@"HID\VID_046D&MI_02\3", "HID Keyboard Device ", "Клавіатури", "HID", "Logitech", "10.0.26100.1", "01-01-2025", DeviceStatus.Ok, 0, true, null),
                new(@"USB\VID_1234&MI_00\1", "USB Audio Device", "Звукові пристрої", "Аудіо", "Realtek", "6.0.9500.1", null, DeviceStatus.Ok, 0, true, null),
                new(@"USB\VID_1234&MI_01\2", "USB Audio Device", "Звукові пристрої", "Аудіо", "Realtek", "6.0.9500.1", null, DeviceStatus.Warning, 28, true, null),
                new(@"PCI\VEN_10DE&DEV_2184", "NVIDIA GeForce GTX 1660", "Відеоадаптери", "PCIe", "NVIDIA", "32.0.16.1088", "10-15-2024", DeviceStatus.Ok, 0, true, null)
            ];

            return Task.FromResult(nodes);
        }
    }

    private sealed class RecordingCleanerService : ICleanerService
    {
        public IReadOnlyList<string> LastAnalyzeExclusions { get; private set; } = [];

        public Task<CleanerScanReport> AnalyzeAsync(
            IReadOnlyList<CleanerRuleDefinition> rules,
            IReadOnlyList<string> exclusions,
            CancellationToken cancellationToken = default,
            IProgress<CleanerProgress>? progress = null)
        {
            LastAnalyzeExclusions = exclusions.ToArray();
            return Task.FromResult(new CleanerScanReport(DateTimeOffset.UtcNow, [], 0, 0));
        }

        public Task<CleanerExecutionResult> CleanAsync(
            CleanerScanReport approvedReport,
            IReadOnlySet<string> selectedRuleIds,
            IReadOnlyList<string> exclusions,
            bool createRestorePointFirst,
            CancellationToken cancellationToken = default,
            IProgress<CleanerProgress>? progress = null)
        {
            return Task.FromResult(new CleanerExecutionResult(DateTimeOffset.UtcNow, 0, 0, 0, null, []));
        }
    }
}
