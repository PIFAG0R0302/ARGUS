using Argus.App.Services;
using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class ProcessesGroupKillTests
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
        private readonly Dictionary<int, SystemActionResult> _customResults = new();

        public List<int> TerminatedPids { get; } = [];
        public int RefreshCount { get; private set; }
        public List<ProcessMetricsItem> Snapshot { get; set; } = [];
        public bool RemoveTerminatedFromSnapshot { get; set; }

        public void SetTerminateResult(int pid, SystemActionResult result) => _customResults[pid] = result;

        public Task<IReadOnlyList<ProcessMetricsItem>> GetProcessMetricsAsync(CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return Task.FromResult<IReadOnlyList<ProcessMetricsItem>>(Snapshot.ToList());
        }

        public Task<SystemActionResult> TerminateProcessAsync(int pid, CancellationToken cancellationToken = default)
        {
            TerminatedPids.Add(pid);
            if (_customResults.TryGetValue(pid, out var custom))
            {
                if (custom.Succeeded && RemoveTerminatedFromSnapshot)
                {
                    Snapshot.RemoveAll(p => p.Pid == pid);
                }

                return Task.FromResult(custom);
            }

            if (RemoveTerminatedFromSnapshot)
            {
                Snapshot.RemoveAll(p => p.Pid == pid);
            }

            return Task.FromResult(new SystemActionResult(true, false, $"Процес (PID {pid}) завершено."));
        }

        public bool OpenProcessFolder(string? executablePath) => !string.IsNullOrWhiteSpace(executablePath);
    }

    private static AppUiModeService CreateUiMode(bool isAdmin = false) =>
        new(new FakeSettingsStore(), new StubPrivilege(isAdmin), new ArgusEventBus(NullLogger<ArgusEventBus>.Instance));

    [Fact]
    public async Task MultiInstanceGroup_OpensConfirmationWithGroupNameAndCount_AndCancelClearsState()
    {
        using var uiMode = CreateUiMode();
        var fakeSvc = new FakeProcessMonitorService
        {
            Snapshot =
            [
                new(501, "chrome", 10.0, 200_000_000, 0, 0, 12, "C:\\Chrome\\chrome.exe", false, false),
                new(502, "chrome", 5.0, 150_000_000, 0, 0, 10, "C:\\Chrome\\chrome.exe", false, false),
                new(503, "chrome", 2.0, 100_000_000, 0, 0, 8, "C:\\Chrome\\chrome.exe", false, false)
            ]
        };

        using var vm = new ProcessesViewModel(fakeSvc, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        var chromeGroup = Assert.Single(vm.ProcessGroups);
        Assert.True(chromeGroup.IsMultiInstance);
        Assert.False(chromeGroup.CanTerminateSingle);
        Assert.True(chromeGroup.CanTerminateGroup);
        Assert.True(chromeGroup.RequestTerminateGroupCommand.CanExecute(null));

        chromeGroup.RequestTerminateGroupCommand.Execute(null);

        Assert.True(vm.IsTerminateConfirmationOpen);
        Assert.Contains("chrome", vm.TerminatePromptText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3", vm.TerminatePromptText, StringComparison.Ordinal);
        Assert.Empty(fakeSvc.TerminatedPids);

        vm.CancelTerminateCommand.Execute(null);
        Assert.False(vm.IsTerminateConfirmationOpen);
        Assert.Equal(string.Empty, vm.TerminatePromptText);

        await vm.ConfirmTerminateAsync();
        Assert.Empty(fakeSvc.TerminatedPids);
    }

    [Fact]
    public async Task MultiInstanceGroup_ConfirmKillsAllNonCriticalInstancesReportsSummaryAndRefreshes()
    {
        using var uiMode = CreateUiMode();
        var fakeSvc = new FakeProcessMonitorService
        {
            RemoveTerminatedFromSnapshot = true,
            Snapshot =
            [
                new(501, "chrome", 10.0, 200_000_000, 0, 0, 12, "C:\\Chrome\\chrome.exe", false, false),
                new(502, "chrome", 5.0, 150_000_000, 0, 0, 10, "C:\\Chrome\\chrome.exe", false, false),
                new(503, "chrome", 2.0, 100_000_000, 0, 0, 8, "C:\\Chrome\\chrome.exe", false, false),
                new(600, "notepad", 0.5, 25_000_000, 0, 0, 4, "C:\\Windows\\System32\\notepad.exe", false, false)
            ]
        };

        using var vm = new ProcessesViewModel(fakeSvc, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();
        int initialRefreshCount = fakeSvc.RefreshCount;

        var chromeGroup = Assert.Single(vm.ProcessGroups, static g => g.Name == "chrome");
        chromeGroup.RequestTerminateGroupCommand.Execute(null);
        Assert.True(vm.IsTerminateConfirmationOpen);

        await vm.ConfirmTerminateAsync();

        Assert.False(vm.IsTerminateConfirmationOpen);
        Assert.Equal([501, 502, 503], fakeSvc.TerminatedPids.OrderBy(static pid => pid).ToArray());
        Assert.True(vm.HasActionFeedback);
        Assert.Contains("3", vm.ActionFeedback, StringComparison.Ordinal);
        Assert.Contains("chrome", vm.ActionFeedback, StringComparison.OrdinalIgnoreCase);
        Assert.True(fakeSvc.RefreshCount > initialRefreshCount);

        // After refresh, chrome group was removed and notepad remains
        var remainingGroup = Assert.Single(vm.ProcessGroups);
        Assert.Equal("notepad", remainingGroup.Name);
    }

    [Fact]
    public async Task MultiInstanceGroup_SkipsCriticalOsProcessesInPromptAndExecution()
    {
        using var uiMode = CreateUiMode();
        var fakeSvc = new FakeProcessMonitorService
        {
            Snapshot =
            [
                // Mixed group in Expert mode: 2 non-critical + 1 critical OS process
                new(701, "worker", 10.0, 200_000_000, 0, 0, 8, "C:\\App\\worker.exe", IsCriticalSystem: false, IsProtectedAccess: false),
                new(702, "worker", 5.0, 100_000_000, 0, 0, 6, "C:\\App\\worker.exe", IsCriticalSystem: false, IsProtectedAccess: false),
                new(703, "worker", 1.0, 50_000_000, 0, 0, 4, "C:\\Windows\\System32\\worker.exe", IsCriticalSystem: true, IsProtectedAccess: false),
                // All-critical group protected by CriticalProcessProtection even if IsCriticalSystem was not pre-flagged
                new(801, "svchost", 2.0, 80_000_000, 0, 0, 10, "C:\\Windows\\System32\\svchost.exe", IsCriticalSystem: false, IsProtectedAccess: false),
                new(802, "svchost", 1.0, 40_000_000, 0, 0, 6, "C:\\Windows\\System32\\svchost.exe", IsCriticalSystem: false, IsProtectedAccess: false)
            ]
        };

        using var vm = new ProcessesViewModel(fakeSvc, uiMode: uiMode, autoLoadOnInit: false);
        vm.IsExpertMode = true;
        await vm.RefreshAsync();

        var svchostGroup = Assert.Single(vm.ProcessGroups, static g => g.Name == "svchost");
        Assert.True(svchostGroup.HasCriticalSystem);
        Assert.False(svchostGroup.CanTerminateGroup);
        Assert.False(svchostGroup.RequestTerminateGroupCommand.CanExecute(null));

        var workerGroup = Assert.Single(vm.ProcessGroups, static g => g.Name == "worker");
        Assert.True(workerGroup.HasCriticalSystem);
        Assert.True(workerGroup.CanTerminateGroup);
        Assert.False(workerGroup.CanTerminateSingle);

        workerGroup.RequestTerminateGroupCommand.Execute(null);
        Assert.True(vm.IsTerminateConfirmationOpen);
        Assert.Contains("worker", vm.TerminatePromptText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3", vm.TerminatePromptText, StringComparison.Ordinal);
        Assert.Contains("2", vm.TerminatePromptText, StringComparison.Ordinal);
        Assert.Contains("Критичні", vm.TerminatePromptText, StringComparison.Ordinal);

        await vm.ConfirmTerminateAsync();

        Assert.Equal([701, 702], fakeSvc.TerminatedPids.OrderBy(static pid => pid).ToArray());
        Assert.DoesNotContain(703, fakeSvc.TerminatedPids);
        Assert.True(vm.HasActionFeedback);
        Assert.Contains("2", vm.ActionFeedback, StringComparison.Ordinal);
        Assert.Contains("1", vm.ActionFeedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiInstanceGroup_PartialFailureAndElevationRequired_ReportsUkrainianSummary()
    {
        using var uiMode = CreateUiMode(isAdmin: false);
        var fakeSvc = new FakeProcessMonitorService
        {
            Snapshot =
            [
                new(901, "helper", 4.0, 120_000_000, 0, 0, 4, "C:\\App\\helper.exe", false, false),
                new(902, "helper", 2.0, 80_000_000, 0, 0, 4, "C:\\App\\helper.exe", false, false)
            ]
        };
        fakeSvc.SetTerminateResult(902, new SystemActionResult(false, RequiresElevation: true, "Потрібні права адміністратора (UAC)."));

        using var vm = new ProcessesViewModel(fakeSvc, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        var helperGroup = Assert.Single(vm.ProcessGroups);
        helperGroup.RequestTerminateGroupCommand.Execute(null);
        await vm.ConfirmTerminateAsync();

        Assert.Equal([901, 902], fakeSvc.TerminatedPids.OrderBy(static pid => pid).ToArray());
        Assert.True(vm.HasActionFeedback);
        Assert.Contains("1 із 2", vm.ActionFeedback, StringComparison.Ordinal);
        Assert.Contains("UAC", vm.ActionFeedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleInstanceGroup_PreservesSingleTerminationBehavior()
    {
        using var uiMode = CreateUiMode();
        var fakeSvc = new FakeProcessMonitorService
        {
            Snapshot =
            [
                new(600, "notepad", 0.5, 25_000_000, 0, 0, 4, "C:\\Windows\\System32\\notepad.exe", false, false)
            ]
        };

        using var vm = new ProcessesViewModel(fakeSvc, uiMode: uiMode, autoLoadOnInit: false);
        await vm.RefreshAsync();

        var notepadGroup = Assert.Single(vm.ProcessGroups);
        Assert.True(notepadGroup.IsSingleInstance);
        Assert.True(notepadGroup.CanTerminateSingle);
        Assert.True(notepadGroup.CanTerminateGroup);

        notepadGroup.RequestTerminateGroupCommand.Execute(null);
        Assert.True(vm.IsTerminateConfirmationOpen);
        Assert.Contains("PID 600", vm.TerminatePromptText, StringComparison.Ordinal);

        await vm.ConfirmTerminateAsync();
        Assert.Equal([600], fakeSvc.TerminatedPids);
    }

    [Fact]
    public async Task IsExpertMode_TogglesSharedModeAndNotifiesPropertyChangedForPathVisibilityBinding()
    {
        using var uiMode = CreateUiMode();
        var fakeSvc = new FakeProcessMonitorService
        {
            Snapshot =
            [
                new(501, "chrome", 10.0, 200_000_000, 0, 0, 12, "C:\\Chrome\\chrome.exe", false, false),
                new(502, "chrome", 5.0, 150_000_000, 0, 0, 10, "C:\\Chrome\\chrome.exe", false, false),
                new(4, "System", 0.5, 40_000_000, 0, 0, 80, null, IsCriticalSystem: true, IsProtectedAccess: true)
            ]
        };

        using var vm = new ProcessesViewModel(fakeSvc, uiMode: uiMode, autoLoadOnInit: false);
        var changedProps = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProps.Add(e.PropertyName);

        await vm.RefreshAsync();

        // Basic mode by default: IsExpertMode is false (UI hides .exe paths in both summary & child rows)
        Assert.False(vm.IsExpertMode);
        var chromeGroup = Assert.Single(vm.ProcessGroups);
        Assert.Equal("C:\\Chrome\\chrome.exe", chromeGroup.ExecutablePath);
        Assert.Equal("C:\\Chrome\\chrome.exe", chromeGroup.Instances[0].ExecutablePath);

        // Switching to Expert mode notifies IsExpertMode and reveals critical processes
        uiMode.IsExpertMode = true;

        Assert.True(vm.IsExpertMode);
        Assert.Contains(nameof(ProcessesViewModel.IsExpertMode), changedProps);
        Assert.Equal(2, vm.ProcessGroups.Count);
    }
}
