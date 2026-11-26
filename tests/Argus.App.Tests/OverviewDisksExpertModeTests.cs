using System.Xml.Linq;
using Argus.App.Services;
using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Devices;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Argus.Core.Journal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class OverviewDisksExpertModeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusOverviewDisksExpertTests_{Guid.NewGuid():N}");

    public OverviewDisksExpertModeTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task OverviewViewModel_SyncsWithAppUiModeService_AndSimplifiesHardwareSummaryInBasicMode()
    {
        using var env = CreateEnvironment();

        await env.Devices.RefreshDevicesAsync();

        // Basic mode by default
        Assert.False(env.UiMode.IsExpertMode);
        Assert.False(env.Overview.IsExpertMode);
        Assert.Equal("AMD Ryzen 7 5800X", env.Overview.CpuArchitectureText);
        Assert.Equal("NVIDIA GeForce RTX 3070", env.Overview.GpuArchitectureText);
        Assert.DoesNotContain("VRAM", env.Overview.GpuArchitectureText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("32 ГіБ · Слотів: 2", env.Overview.RamArchitectureText);
        Assert.DoesNotContain("DIMM_A1", env.Overview.RamArchitectureText, StringComparison.Ordinal);

        // Track PropertyChanged notifications for IsExpertMode
        var changedProps = new List<string?>();
        env.Overview.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        // Switch to Expert mode via shared AppUiModeService
        env.UiMode.SetExpertMode(true, persist: false);

        Assert.True(env.Overview.IsExpertMode);
        Assert.Contains(nameof(OverviewViewModel.IsExpertMode), changedProps);
        Assert.Equal("AMD Ryzen 7 5800X", env.Overview.CpuArchitectureText);
        Assert.Equal("NVIDIA GeForce RTX 3070 · 8 ГіБ VRAM", env.Overview.GpuArchitectureText);
        Assert.Contains("DIMM_A1", env.Overview.RamArchitectureText, StringComparison.Ordinal);
        Assert.Contains("DIMM_B1", env.Overview.RamArchitectureText, StringComparison.Ordinal);

        // Switch back to Basic mode
        env.UiMode.SetExpertMode(false, persist: false);

        Assert.False(env.Overview.IsExpertMode);
        Assert.Equal("NVIDIA GeForce RTX 3070", env.Overview.GpuArchitectureText);
        Assert.DoesNotContain("DIMM_A1", env.Overview.RamArchitectureText, StringComparison.Ordinal);
    }

    [Fact]
    public void OverviewViewModel_BackwardCompatibleConstructor_WorksWithoutUiMode()
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var settingsStore = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, $"legacy_settings_{Guid.NewGuid():N}.json"));
        using var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, $"legacy_journal_{Guid.NewGuid():N}.jsonl"));

        using var vm = new OverviewViewModel([], settingsStore, journal, bus);

        Assert.False(vm.IsExpertMode);
        vm.IsExpertMode = true;
        Assert.True(vm.IsExpertMode);
    }

    [Fact]
    public async Task DisksViewModel_SyncsWithAppUiModeService_AndPreservesDiskCardAndSpaceMapProperties()
    {
        using var env = CreateEnvironment();

        await env.Disks.RefreshDisksAsync();

        Assert.False(env.Disks.IsExpertMode);
        Assert.Equal(2, env.Disks.Disks.Count);

        var ssd = env.Disks.Disks[0];
        Assert.Equal("Диск 0 · Samsung SSD 980 PRO", ssd.Title);
        Assert.Equal("NVMe SSD", ssd.MediaBadge);
        Assert.False(string.IsNullOrWhiteSpace(ssd.BasicCapacityText));
        Assert.False(string.IsNullOrWhiteSpace(ssd.BusAndCapacityText));
        Assert.Equal("Здоровий", ssd.HealthLabel);
        Assert.Equal("Оптимізація SSD (C:)", ssd.OptimizeButtonLabel);
        Assert.True(ssd.OptimizeCommand.CanExecute(null));

        Assert.Single(env.Disks.LargestFolders);
        Assert.Equal("Games", env.Disks.LargestFolders[0].Name);

        // Sync via AppUiModeService
        env.UiMode.SetExpertMode(true, persist: false);
        Assert.True(env.Disks.IsExpertMode);
        Assert.True(env.Overview.IsExpertMode);
        Assert.Equal(2, env.Overview.ConnectedDisks.Count);
        Assert.Equal("Диск 0 · Samsung SSD 980 PRO", env.Overview.ConnectedDisks[0].Title);
        Assert.Equal("NVMe SSD", env.Overview.ConnectedDisks[0].MediaBadge);
        Assert.Equal("Здоровий", env.Overview.ConnectedDisks[0].HealthLabel);

        env.UiMode.SetExpertMode(false, persist: false);
        Assert.False(env.Disks.IsExpertMode);
    }

    [Fact]
    public void OverviewView_GatesSubCaptionsNodeParamsAndDiskDetailsBehindIsExpertMode()
    {
        string viewPath = Path.Combine(FindRepositoryRoot(), "src", "Argus.App", "Views", "OverviewView.axaml");
        Assert.True(File.Exists(viewPath), $"Expected view file at {viewPath}");

        var doc = XDocument.Load(viewPath);
        XNamespace ns = "https://github.com/avaloniaui";

        var textBlocks = doc.Descendants(ns + "TextBlock").ToList();
        var grids = doc.Descendants(ns + "Grid").ToList();

        // CPU & GPU architecture sub-captions and BIOS sub-line must be hidden in Basic mode
        AssertIsVisible(textBlocks, "Text", "{Binding CpuCacheDetailText}", "{Binding IsExpertMode}");
        AssertIsVisible(textBlocks, "Text", "{Binding LiveGpuSummaryText}", "{Binding IsExpertMode}");
        AssertIsVisible(textBlocks, "Text", "{Binding BiosArchitectureText}", "{Binding IsExpertMode}");

        // CPU & GPU primary name TextBlocks stay visible unconditionally
        var cpuName = Assert.Single(textBlocks, static t => (string?)t.Attribute("Text") == "{Binding CpuArchitectureText}");
        Assert.Null(cpuName.Attribute("IsVisible"));

        var gpuName = Assert.Single(textBlocks, static t => (string?)t.Attribute("Text") == "{Binding GpuArchitectureText}");
        Assert.Null(gpuName.Attribute("IsVisible"));

        // Node parameters: TelemetryIntervalText & SensorTierText rows hidden in Basic mode
        Assert.Contains(grids, g =>
            (string?)g.Attribute("IsVisible") == "{Binding IsExpertMode}" &&
            g.Descendants(ns + "TextBlock").Any(t => (string?)t.Attribute("Text") == "{Binding TelemetryIntervalText}"));
        Assert.Contains(grids, g =>
            (string?)g.Attribute("IsVisible") == "{Binding IsExpertMode}" &&
            g.Descendants(ns + "TextBlock").Any(t => (string?)t.Attribute("Text") == "{Binding SensorTierText}"));

        // ConnectedDisks DataTemplate: only Title is visible in Basic mode; MediaBadge, DetailsText, HealthLabel require IsExpertMode
        const string diskTemplateExpertBinding = "{Binding $parent[UserControl].((vm:OverviewViewModel)DataContext).IsExpertMode}";

        var connectedDisksTemplate = doc
            .Descendants(ns + "ItemsControl")
            .First(ic => (string?)ic.Attribute("ItemsSource") == "{Binding ConnectedDisks}");

        var diskTitle = connectedDisksTemplate
            .Descendants(ns + "TextBlock")
            .First(t => (string?)t.Attribute("Text") == "{Binding Title}");
        Assert.Null(diskTitle.Attribute("IsVisible"));

        var mediaBadgeBorder = connectedDisksTemplate
            .Descendants(ns + "Border")
            .First(b => b.Elements(ns + "TextBlock").Any(t => (string?)t.Attribute("Text") == "{Binding MediaBadge}"));
        Assert.Equal(diskTemplateExpertBinding, (string?)mediaBadgeBorder.Attribute("IsVisible"));

        var diskDetailsText = connectedDisksTemplate
            .Descendants(ns + "TextBlock")
            .First(t => (string?)t.Attribute("Text") == "{Binding DetailsText}");
        Assert.Equal(diskTemplateExpertBinding, (string?)diskDetailsText.Attribute("IsVisible"));

        var healthBorder = connectedDisksTemplate
            .Descendants(ns + "Border")
            .First(b => b.Elements(ns + "TextBlock").Any(t => (string?)t.Attribute("Text") == "{Binding HealthLabel}"));
        Assert.Equal(diskTemplateExpertBinding, (string?)healthBorder.Attribute("IsVisible"));
    }

    [Fact]
    public void DisksView_RemovesInlineExpertToggleAndGatesTechnicalDetailsBehindIsExpertMode()
    {
        string viewPath = Path.Combine(FindRepositoryRoot(), "src", "Argus.App", "Views", "DisksView.axaml");
        Assert.True(File.Exists(viewPath), $"Expected view file at {viewPath}");

        string rawXaml = File.ReadAllText(viewPath);
        Assert.DoesNotContain("expert-toggle-bar", rawXaml, StringComparison.Ordinal);

        var doc = XDocument.Load(viewPath);
        XNamespace ns = "https://github.com/avaloniaui";

        // Refresh button remains present
        Assert.Contains(doc.Descendants(ns + "Button"), static b =>
            (string?)b.Attribute("Content") == "Оновити диски" &&
            (string?)b.Attribute("Command") == "{Binding RefreshDisksCommand}");

        // PhysicalDiskCardItem DataTemplate: Title and Optimize button remain unconditionally visible;
        // MediaBadge, BasicCapacityText, Expert strip, and S.M.A.R.T. health/temp pills require IsExpertMode
        const string diskTemplateExpertBinding = "{Binding $parent[UserControl].((vm:DisksViewModel)DataContext).IsExpertMode}";

        var disksList = doc
            .Descendants(ns + "ItemsControl")
            .First(ic => (string?)ic.Attribute("ItemsSource") == "{Binding Disks}");

        var diskTitle = disksList
            .Descendants(ns + "TextBlock")
            .First(t => (string?)t.Attribute("Text") == "{Binding Title}");
        Assert.Null(diskTitle.Attribute("IsVisible"));

        var optimizeButton = disksList
            .Descendants(ns + "Button")
            .First(b => (string?)b.Attribute("Command") == "{Binding OptimizeCommand}");
        Assert.Equal("{Binding HasDriveLetter}", (string?)optimizeButton.Attribute("IsVisible"));

        var mediaBadgeBorder = disksList
            .Descendants(ns + "Border")
            .First(b => b.Elements(ns + "TextBlock").Any(t => (string?)t.Attribute("Text") == "{Binding MediaBadge}"));
        Assert.Equal(diskTemplateExpertBinding, (string?)mediaBadgeBorder.Attribute("IsVisible"));

        var basicCapacity = disksList
            .Descendants(ns + "TextBlock")
            .First(t => (string?)t.Attribute("Text") == "{Binding BasicCapacityText}");
        Assert.Equal(diskTemplateExpertBinding, (string?)basicCapacity.Attribute("IsVisible"));

        var expertStrip = disksList
            .Descendants(ns + "Border")
            .First(b => b.Elements(ns + "StackPanel").Elements(ns + "TextBlock").Any(t => (string?)t.Attribute("Text") == "{Binding SmartDetailsText}"));
        Assert.Equal(diskTemplateExpertBinding, (string?)expertStrip.Attribute("IsVisible"));

        var smartBadgesPanel = disksList
            .Descendants(ns + "StackPanel")
            .First(sp => sp.Descendants(ns + "TextBlock").Any(t => (string?)t.Attribute("Text") == "{Binding HealthLabel}"));
        Assert.Equal(diskTemplateExpertBinding, (string?)smartBadgesPanel.Attribute("IsVisible"));

        // Space map and Recycle Bin confirmation remain intact and are not gated behind IsExpertMode
        var spaceMapList = doc
            .Descendants(ns + "ItemsControl")
            .First(ic => (string?)ic.Attribute("ItemsSource") == "{Binding LargestFolders}");
        Assert.Null(spaceMapList.Attribute("IsVisible"));

        var deleteBanner = doc
            .Descendants(ns + "Border")
            .First(b => (string?)b.Attribute("IsVisible") == "{Binding IsDeleteConfirmationOpen}");
        Assert.NotNull(deleteBanner);
    }

    private static void AssertIsVisible(
        IEnumerable<XElement> elements,
        string matchAttribute,
        string matchValue,
        string expectedIsVisible)
    {
        var element = Assert.Single(elements, e => (string?)e.Attribute(matchAttribute) == matchValue);
        Assert.Equal(expectedIsVisible, (string?)element.Attribute("IsVisible"));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Argus.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Argus.sln above test output directory.");
    }

    private TestEnvironment CreateEnvironment()
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        var settingsStore = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, $"settings_{Guid.NewGuid():N}.json"));
        var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, $"journal_{Guid.NewGuid():N}.jsonl"));
        var uiMode = new AppUiModeService(settingsStore, new StubPrivilegeService(false), bus);

        var devices = new DevicesViewModel(new StubDeviceProvider(), bus, autoLoadOnInit: false, uiMode: uiMode);
        var disks = new DisksViewModel(new StubDiskOptimizer(), new StubSpaceAnalyzer(), autoLoadOnInit: false, uiMode: uiMode);
        var overview = new OverviewViewModel(
            [],
            settingsStore,
            journal,
            bus,
            devicesViewModel: devices,
            disksViewModel: disks,
            uiMode: uiMode);

        return new TestEnvironment(settingsStore, journal, uiMode, devices, disks, overview);
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
            // best-effort cleanup
        }
    }

    private sealed class TestEnvironment(
        JsonArgusSettingsStore settingsStore,
        FileBackedOperationJournal journal,
        AppUiModeService uiMode,
        DevicesViewModel devices,
        DisksViewModel disks,
        OverviewViewModel overview) : IDisposable
    {
        public AppUiModeService UiMode { get; } = uiMode;
        public DevicesViewModel Devices { get; } = devices;
        public DisksViewModel Disks { get; } = disks;
        public OverviewViewModel Overview { get; } = overview;

        public void Dispose()
        {
            Overview.Dispose();
            Disks.Dispose();
            Devices.Dispose();
            UiMode.Dispose();
            journal.Dispose();
            settingsStore.Dispose();
        }
    }

    private sealed class StubPrivilegeService(bool isAdmin) : IPrivilegeService
    {
        public bool IsAdministrator { get; } = isAdmin;
        public bool TryRelaunchElevated(string[]? arguments = null) => false;
    }

    private sealed class StubDeviceProvider : IDeviceProvider
    {
        public Task<HardwareInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
        {
            var inventory = new HardwareInventory(
                DateTimeOffset.UtcNow,
                new OsVersionInfo("Windows 11 Pro", "24H2", "26100", "X64", IsUefiFirmware: true),
                new CpuSpec("AMD Ryzen 7 5800X", "AuthenticAMD", "X64", "AM4", 8, 16, 4700, 4 * 1024 * 1024, 32 * 1024 * 1024),
                [
                    new RamModuleSpec("DIMM_A1", "Kingston", "KF3200C16", 16L * 1024 * 1024 * 1024, 3200, "DDR4"),
                    new RamModuleSpec("DIMM_B1", "Kingston", "KF3200C16", 16L * 1024 * 1024 * 1024, 3200, "DDR4")
                ],
                new MotherboardSpec("ASUS", "ROG STRIX B550-F", "AMI", "3205", "2024-03-14"),
                [new GpuSpec("NVIDIA GeForce RTX 3070", "NVIDIA", "32.0.15.6094", 8L * 1024 * 1024 * 1024, @"PCI\VEN_10DE&DEV_2484")],
                [new NetworkAdapterSpec("Intel I225-V", "00:11:22:33:44:55", 2_500_000_000, true, "Ethernet")]);

            return Task.FromResult(inventory);
        }

        public Task<IReadOnlyList<DeviceNode>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceNode>>([]);
    }

    private sealed class StubDiskOptimizer : IDiskOptimizer
    {
        public Task<IReadOnlyList<PhysicalDiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<PhysicalDiskInfo> disks =
            [
                new(0, "Samsung SSD 980 PRO", "SN-NVME-0", "NVMe", StorageMediaKind.NvmeSsd, 1_000_000_000_000, "GPT", ['C'], new SmartSummary(SmartHealthStatus.Healthy, 38, 99, 1400, 0, 0)),
                new(1, "WDC WD20EZBX", "SN-HDD-1", "SATA", StorageMediaKind.Hdd, 2_000_000_000_000, "GPT", ['D'], new SmartSummary(SmartHealthStatus.Healthy, 33, null, 4100, 0, 0))
            ];

            return Task.FromResult(disks);
        }

        public Task<DiskOptimizationResult> OptimizeVolumeAsync(
            char driveLetter,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiskOptimizationResult(
                driveLetter,
                StorageMediaKind.NvmeSsd,
                DiskOptimizationAction.TrimSsd,
                Succeeded: true,
                TimeSpan.FromSeconds(1),
                $"TRIM тома {driveLetter}: завершено."));
    }

    private sealed class StubSpaceAnalyzer : IDiskSpaceAnalyzer
    {
        public Task<VolumeSpaceReport> AnalyzeVolumeSpaceAsync(
            char driveLetter,
            int topFolderCount = 8,
            CancellationToken cancellationToken = default,
            IProgress<(int Percent, string FolderName)>? progress = null) =>
            Task.FromResult(new VolumeSpaceReport(
                driveLetter,
                "System",
                "NTFS",
                500_000_000_000,
                200_000_000_000,
                300_000_000_000,
                [new FolderSpaceNode("Games", $@"{driveLetter}:\Games", 50_000_000_000, 100, 25.0, 100.0)]));
    }
}
