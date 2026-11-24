using Argus.App.Services;
using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class UxProcessesUpdateTests
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

    private sealed class FakeProcessMonitorService : IProcessMonitorService
    {
        public List<int> TerminatedPids { get; } = [];
        public List<string?> OpenedPaths { get; } = [];
        public IReadOnlyList<ProcessMetricsItem> Snapshot { get; set; } = [];

        public Task<IReadOnlyList<ProcessMetricsItem>> GetProcessMetricsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default)
        {
            TerminatedPids.Add(pid);
            return Task.FromResult(new SystemActionResult(true, false, $"Terminated {pid}"));
        }

        public bool OpenProcessFolder(string? executablePath)
        {
            OpenedPaths.Add(executablePath);
            return !string.IsNullOrWhiteSpace(executablePath);
        }
    }

    [Fact]
    public async Task ProcessGroups_GroupsByNormalizedNameAggregatesMetricsAndStartsCollapsed()
    {
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), new StubPrivilege(false), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var fakeSvc = new FakeProcessMonitorService
        {
            Snapshot =
            [
                new(5010, "chrome", 12.5, 400_000_000, 10_000, 5_000, 30, "C:\\Program Files\\Google\\Chrome\\chrome.exe", IsCriticalSystem: false, IsProtectedAccess: false),
                new(5020, "Chrome ", 7.5, 250_000_000, 20_000, 15_000, 20, null, IsCriticalSystem: false, IsProtectedAccess: false),
                new(6000, "notepad", 0.5, 25_000_000, 0, 0, 4, "C:\\Windows\\System32\\notepad.exe", IsCriticalSystem: false, IsProtectedAccess: false),
                new(4, "System", 1.0, 50_000_000, 0, 0, 100, null, IsCriticalSystem: true, IsProtectedAccess: true)
            ]
        };

        using var vm = new ProcessesViewModel(fakeSvc, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        // Basic mode hides critical "System"
        Assert.Equal(2, vm.ProcessGroups.Count);

        // Default sort is RAM descending -> chrome group (650 MB) before notepad (25 MB)
        var chromeGroup = vm.ProcessGroups[0];
        Assert.Equal("chrome", chromeGroup.Name, ignoreCase: true);
        Assert.Equal(2, chromeGroup.InstanceCount);
        Assert.True(chromeGroup.IsMultiInstance);
        Assert.False(chromeGroup.IsSingleInstance);
        Assert.Equal("2 проц.", chromeGroup.PidOrCountText);
        Assert.Equal(20.0, chromeGroup.TotalCpuPercent, precision: 2);
        Assert.Equal(650_000_000, chromeGroup.TotalWorkingSetBytes);
        Assert.Equal(50_000.0, chromeGroup.TotalDiskBytesPerSec, precision: 2);
        Assert.Equal(50, chromeGroup.TotalThreadCount);
        Assert.Equal("50 пот.", chromeGroup.ThreadsText);
        Assert.Equal("C:\\Program Files\\Google\\Chrome\\chrome.exe", chromeGroup.ExecutablePath);
        Assert.True(chromeGroup.HasExecutablePath);
        Assert.False(chromeGroup.HasCriticalSystem);
        Assert.False(chromeGroup.CanTerminateSingle);
        Assert.Equal(2, chromeGroup.Instances.Count);

        // All groups start collapsed by default
        Assert.False(chromeGroup.IsExpanded);
        Assert.Equal("▶", chromeGroup.ExpandGlyph);

        var notepadGroup = vm.ProcessGroups[1];
        Assert.Equal("notepad", notepadGroup.Name);
        Assert.Equal(1, notepadGroup.InstanceCount);
        Assert.True(notepadGroup.IsSingleInstance);
        Assert.False(notepadGroup.IsMultiInstance);
        Assert.Equal("6000", notepadGroup.PidOrCountText);
        Assert.True(notepadGroup.CanTerminateSingle);
        Assert.False(notepadGroup.IsExpanded);

        // Toggle expand/collapse
        chromeGroup.ToggleExpandedCommand.Execute(null);
        Assert.True(chromeGroup.IsExpanded);
        Assert.Equal("▼", chromeGroup.ExpandGlyph);

        chromeGroup.ToggleExpandedCommand.Execute(null);
        Assert.False(chromeGroup.IsExpanded);
        Assert.Equal("▶", chromeGroup.ExpandGlyph);

        // Open folder uses representative executable path
        chromeGroup.OpenFolderCommand.Execute(null);
        Assert.Single(fakeSvc.OpenedPaths);
        Assert.Equal("C:\\Program Files\\Google\\Chrome\\chrome.exe", fakeSvc.OpenedPaths[0]);
    }

    [Fact]
    public async Task ProcessGroups_SyncsInPlacePreservingExpandedStateSelectionAndChildRows()
    {
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), new StubPrivilege(false), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var fakeSvc = new FakeProcessMonitorService
        {
            Snapshot =
            [
                new(100, "app", 5.0, 100_000_000, 0, 0, 10, "C:\\app\\app.exe", false, false),
                new(200, "app", 2.0, 50_000_000, 0, 0, 5, "C:\\app\\app.exe", false, false),
                new(300, "editor", 1.0, 80_000_000, 0, 0, 8, "C:\\editor\\editor.exe", false, false)
            ]
        };

        using var vm = new ProcessesViewModel(fakeSvc, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        var appGroup = Assert.Single(vm.ProcessGroups, static g => g.GroupKey.Equals("app", StringComparison.OrdinalIgnoreCase));
        var pid100Row = Assert.Single(appGroup.Instances, static r => r.Pid == 100);
        appGroup.ToggleExpandedCommand.Execute(null);
        vm.SelectedGroup = appGroup;

        Assert.True(appGroup.IsExpanded);

        // Simulate 2-second refresh: PID 200 exits, PID 400 spawns, editor spikes above app in RAM
        fakeSvc.Snapshot =
        [
            new(100, "app", 15.0, 120_000_000, 0, 0, 12, "C:\\app\\app.exe", false, false),
            new(400, "app", 3.0, 60_000_000, 0, 0, 6, "C:\\app\\app.exe", false, false),
            new(300, "editor", 25.0, 500_000_000, 0, 0, 16, "C:\\editor\\editor.exe", false, false)
        ];

        await vm.RefreshAsync();

        // Editor moved to index 0, app moved to index 1, same group instance and IsExpanded preserved
        Assert.Equal("editor", vm.ProcessGroups[0].Name);
        Assert.Same(appGroup, vm.ProcessGroups[1]);
        Assert.Same(appGroup, vm.SelectedGroup);
        Assert.True(appGroup.IsExpanded);
        Assert.Equal(18.0, appGroup.TotalCpuPercent, precision: 2);
        Assert.Equal(180_000_000, appGroup.TotalWorkingSetBytes);

        // Child row for PID 100 was updated in-place (same object reference)
        Assert.Equal([100, 400], appGroup.Instances.Select(static r => r.Pid).ToArray());
        Assert.Same(pid100Row, appGroup.Instances[0]);
        Assert.Contains("15", pid100Row.CpuText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessGroups_SortsDeterministicallyByAggregatedMetrics()
    {
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), new StubPrivilege(false), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var fakeSvc = new FakeProcessMonitorService
        {
            Snapshot =
            [
                // "alpha": 2 instances -> CPU 15%, RAM 200 MB, Disk 50 KB/s, lowest PID 100
                new(100, "alpha", 10.0, 100_000_000, 25_000, 0, 4, "C:\\alpha.exe", false, false),
                new(101, "alpha", 5.0, 100_000_000, 25_000, 0, 4, "C:\\alpha.exe", false, false),
                // "beta": 1 instance -> CPU 20%, RAM 100 MB, Disk 500 KB/s, lowest PID 50
                new(50, "beta", 20.0, 100_000_000, 300_000, 200_000, 6, "C:\\beta.exe", false, false),
                // "gamma": 1 instance -> CPU 15%, RAM 200 MB, Disk 50 KB/s, lowest PID 200 (ties alpha on CPU/RAM/Disk)
                new(200, "gamma", 15.0, 200_000_000, 50_000, 0, 2, "C:\\gamma.exe", false, false)
            ]
        };

        using var vm = new ProcessesViewModel(fakeSvc, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        // Default RAM desc: alpha and gamma tie at 200 MB / 15% CPU -> Name tie-breaker puts alpha before gamma
        Assert.Equal(["alpha", "gamma", "beta"], vm.ProcessGroups.Select(static g => g.Name).ToArray());

        // CPU desc: beta (20%) -> alpha (15%) -> gamma (15%)
        vm.SetSortCommand.Execute("CPU");
        Assert.Equal(["beta", "alpha", "gamma"], vm.ProcessGroups.Select(static g => g.Name).ToArray());

        // DISK desc: beta (500 KB/s) -> alpha (50 KB/s) -> gamma (50 KB/s)
        vm.SetSortCommand.Execute("DISK");
        Assert.Equal(["beta", "alpha", "gamma"], vm.ProcessGroups.Select(static g => g.Name).ToArray());

        // NAME asc (first click on NAME is ascending): alpha -> beta -> gamma
        vm.SetSortCommand.Execute("NAME");
        Assert.False(vm.SortDescending);
        Assert.Equal(["alpha", "beta", "gamma"], vm.ProcessGroups.Select(static g => g.Name).ToArray());

        // NAME desc (second click toggles): gamma -> beta -> alpha
        vm.SetSortCommand.Execute("NAME");
        Assert.True(vm.SortDescending);
        Assert.Equal(["gamma", "beta", "alpha"], vm.ProcessGroups.Select(static g => g.Name).ToArray());

        // PID asc (first click on PID is ascending by lowest PID): beta (50) -> alpha (100) -> gamma (200)
        vm.SetSortCommand.Execute("PID");
        Assert.False(vm.SortDescending);
        Assert.Equal(["beta", "alpha", "gamma"], vm.ProcessGroups.Select(static g => g.Name).ToArray());
    }

    [Fact]
    public async Task ProcessGroups_TerminateWorksForSingleGroupAndIndividualChildRowsAndRespectsCriticalProtection()
    {
        using var uiMode = new AppUiModeService(new FakeSettingsStore(), new StubPrivilege(false), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));
        var fakeSvc = new FakeProcessMonitorService
        {
            Snapshot =
            [
                new(500, "chrome", 8.0, 200_000_000, 0, 0, 10, "C:\\chrome.exe", false, false),
                new(501, "chrome", 4.0, 100_000_000, 0, 0, 8, "C:\\chrome.exe", false, false),
                new(600, "notepad", 0.1, 20_000_000, 0, 0, 2, "C:\\notepad.exe", false, false),
                new(4, "System", 0.5, 40_000_000, 0, 0, 80, null, IsCriticalSystem: true, IsProtectedAccess: true)
            ]
        };

        using var vm = new ProcessesViewModel(fakeSvc, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        // Single-instance group can terminate via summary row command with mandatory confirmation
        var notepadGroup = Assert.Single(vm.ProcessGroups, static g => g.Name == "notepad");
        Assert.True(notepadGroup.CanTerminateSingle);
        notepadGroup.RequestTerminateSingleCommand.Execute(null);
        Assert.True(vm.IsTerminateConfirmationOpen);
        await vm.ConfirmTerminateAsync();
        Assert.Equal([600], fakeSvc.TerminatedPids);

        // Multi-instance group terminates individual child instances
        var chromeGroup = Assert.Single(vm.ProcessGroups, static g => g.Name == "chrome");
        Assert.False(chromeGroup.CanTerminateSingle);
        var child501 = Assert.Single(chromeGroup.Instances, static r => r.Pid == 501);
        child501.RequestTerminateCommand.Execute(null);
        Assert.True(vm.IsTerminateConfirmationOpen);
        await vm.ConfirmTerminateAsync();
        Assert.Equal([600, 501], fakeSvc.TerminatedPids);

        // Expert mode shows critical system group with HasCriticalSystem=true and CanTerminateSingle=false
        vm.IsExpertMode = true;
        var systemGroup = Assert.Single(vm.ProcessGroups, static g => g.Name == "System");
        Assert.True(systemGroup.HasCriticalSystem);
        Assert.False(systemGroup.CanTerminateSingle);
        Assert.False(systemGroup.Instances[0].CanTerminate);
    }
}
