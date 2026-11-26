using System.Xml.Linq;
using Argus.App.ViewModels;
using Argus.Core.Contracts.Devices;
using Xunit;

namespace Argus.App.Tests;

public sealed class DevicesExpertModeTests
{
    [Fact]
    public async Task DevicesViewModel_PrimaryGpuTitle_HidesVramInBasicMode_AndShowsInExpertMode()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new StubDeviceProvider();
        using var vm = new DevicesViewModel(provider, autoLoadOnInit: false);

        await vm.RefreshDevicesCommand.ExecuteAsync(null);
        Assert.False(vm.IsExpertMode);
        Assert.Equal("Intel Core i7-9700K CPU", vm.CpuTitle);
        Assert.Equal("ASUS · PRIME Z390-A", vm.BoardTitle);
        Assert.Equal("NVIDIA GeForce RTX 3070", vm.PrimaryGpuTitle);

        vm.IsExpertMode = true;
        Assert.Contains("8", vm.PrimaryGpuTitle, StringComparison.Ordinal);
        Assert.Contains("VRAM", vm.PrimaryGpuTitle, StringComparison.Ordinal);

        vm.IsExpertMode = false;
        Assert.Equal("NVIDIA GeForce RTX 3070", vm.PrimaryGpuTitle);
        await Task.CompletedTask;
        _ = ct;
    }

    [Fact]
    public void DevicesView_GatesCpuThreadsBiosAndDriverDetailsBehindIsExpertMode()
    {
        string viewPath = Path.Combine(FindRepositoryRoot(), "src", "Argus.App", "Views", "DevicesView.axaml");
        Assert.True(File.Exists(viewPath), $"Expected view file at {viewPath}");

        var doc = XDocument.Load(viewPath);
        XNamespace ns = "https://github.com/avaloniaui";

        var textBlocks = doc.Descendants(ns + "TextBlock").ToList();

        // Titles remain unconditionally visible in Basic mode
        foreach (string binding in new[] { "{Binding CpuTitle}", "{Binding BoardTitle}", "{Binding PrimaryGpuTitle}" })
        {
            var tb = textBlocks.First(t => (string?)t.Attribute("Text") == binding);
            Assert.Null(tb.Attribute("IsVisible"));
        }

        // Technical details (cores/threads, cache, BIOS, OS/UEFI, GPU driver, NIC) require IsExpertMode
        foreach (string binding in new[]
        {
            "{Binding CpuTopologyAndClockText}",
            "{Binding CpuCacheAndArchText}",
            "{Binding BiosSummaryText}",
            "{Binding OsAndFirmwareText}",
            "{Binding PrimaryGpuDriverAndIdText}",
            "{Binding PrimaryNicSummaryText}"
        })
        {
            var tb = textBlocks.First(t => (string?)t.Attribute("Text") == binding);
            Assert.Equal("{Binding IsExpertMode}", (string?)tb.Attribute("IsVisible"));
        }

        // Grouped device row BasicDriverText also requires IsExpertMode
        var basicDriver = textBlocks.First(t => (string?)t.Attribute("Text") == "{Binding BasicDriverText}");
        Assert.Equal("{Binding $parent[UserControl].((vm:DevicesViewModel)DataContext).IsExpertMode}",
            (string?)basicDriver.Attribute("IsVisible"));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ARGUS.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ARGUS.sln from test output directory.");
    }

    private sealed class StubDeviceProvider : IDeviceProvider
    {
        public Task<IReadOnlyList<DeviceNode>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceNode>>([]);

        public Task<HardwareInventory> GetInventoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareInventory(
                DateTimeOffset.UtcNow,
                new OsVersionInfo("Windows 11 Pro", "23H2", "22631", "X64", IsUefiFirmware: true),
                new CpuSpec("Intel Core i7-9700K CPU", "GenuineIntel", "x64", "LGA1151", 8, 8, 3600, 2L * 1024 * 1024, 12L * 1024 * 1024),
                [],
                new MotherboardSpec("ASUS", "PRIME Z390-A", "American Megatrends Inc.", "2004", "2021-01-01"),
                [new GpuSpec("NVIDIA GeForce RTX 3070", "NVIDIA", "31.0.15.5123", 8L * 1024 * 1024 * 1024, @"PCI\VEN_10DE")],
                []));
    }
}
