using System.Xml.Linq;
using Argus.App.ViewModels;
using Argus.Core.Contracts.System;
using Xunit;

namespace Argus.App.Tests;

public sealed class StartupFilterTests
{
    [Fact]
    public async Task StateFilters_WorkCleanlyAcrossAllStartupSources()
    {
        var manager = new AllSourcesStartupManager();
        using var vm = new StartupAndServicesViewModel(manager, autoLoadOnInit: false);

        await vm.RefreshAsync();

        // Default state: all sources & all states visible
        Assert.True(vm.IsStartupSourceAll);
        Assert.True(vm.IsStartupStateAll);
        Assert.False(vm.IsStartupStateEnabled);
        Assert.False(vm.IsStartupStateDisabled);
        Assert.Equal(6, vm.StartupEntries.Count);

        // Filter: Enabled -> includes enabled entries from Registry (HKCU, WOW64), StartupFolder (User), TaskScheduler
        vm.SetStartupStateFilterCommand.Execute("Увімкнено");
        Assert.False(vm.IsStartupStateAll);
        Assert.True(vm.IsStartupStateEnabled);
        Assert.False(vm.IsStartupStateDisabled);
        Assert.Equal(3, vm.StartupEntries.Count);
        Assert.All(vm.StartupEntries, static entry => Assert.True(entry.IsEnabled));
        Assert.Contains(vm.StartupEntries, static e => e.Model.Source == StartupSourceKind.RegistryCurrentUserRun);
        Assert.Contains(vm.StartupEntries, static e => e.Model.Source == StartupSourceKind.StartupFolderUser);
        Assert.Contains(vm.StartupEntries, static e => e.Model.Source == StartupSourceKind.TaskScheduler);

        // Filter: Disabled -> includes disabled entries from Registry (HKLM, WOW64), StartupFolder (Common)
        vm.SetStartupStateFilterCommand.Execute("Вимкнено");
        Assert.False(vm.IsStartupStateAll);
        Assert.False(vm.IsStartupStateEnabled);
        Assert.True(vm.IsStartupStateDisabled);
        Assert.Equal(3, vm.StartupEntries.Count);
        Assert.All(vm.StartupEntries, static entry => Assert.False(entry.IsEnabled));
        Assert.Contains(vm.StartupEntries, static e => e.Model.Source == StartupSourceKind.RegistryLocalMachineRun);
        Assert.Contains(vm.StartupEntries, static e => e.Model.Source == StartupSourceKind.RegistryLocalMachineWow6432Run);
        Assert.Contains(vm.StartupEntries, static e => e.Model.Source == StartupSourceKind.StartupFolderCommon);

        // Filter: All states -> restores all 6 entries across every source kind
        vm.SetStartupStateFilterCommand.Execute("Усі стани");
        Assert.True(vm.IsStartupStateAll);
        Assert.Equal(6, vm.StartupEntries.Count);
    }

    [Fact]
    public async Task SetStartupStateFilter_EmptyOrWhitespaceResetsToAllStates()
    {
        var manager = new AllSourcesStartupManager();
        using var vm = new StartupAndServicesViewModel(manager, autoLoadOnInit: false);

        await vm.RefreshAsync();

        vm.SetStartupStateFilterCommand.Execute("Увімкнено");
        Assert.Equal(3, vm.StartupEntries.Count);

        vm.SetStartupStateFilterCommand.Execute("   ");
        Assert.True(vm.IsStartupStateAll);
        Assert.Equal("Усі стани", vm.StartupStateFilter);
        Assert.Equal(6, vm.StartupEntries.Count);
    }

    [Fact]
    public async Task BackwardCompatibility_SourceFilterPropertiesAndCommandStillWork()
    {
        var manager = new AllSourcesStartupManager();
        using var vm = new StartupAndServicesViewModel(manager, autoLoadOnInit: false);

        await vm.RefreshAsync();

        vm.SetStartupFilterCommand.Execute("Registry");
        Assert.True(vm.IsStartupSourceRegistry);
        Assert.Equal(3, vm.StartupEntries.Count);

        vm.SetStartupFilterCommand.Execute("Усі");
        Assert.True(vm.IsStartupSourceAll);
        Assert.Equal(6, vm.StartupEntries.Count);
    }

    [Fact]
    public void StartupAndServicesView_ContainsOnlyStateFilterButtonsInStartupBar()
    {
        string viewPath = Path.Combine(FindRepositoryRoot(), "src", "Argus.App", "Views", "StartupAndServicesView.axaml");
        Assert.True(File.Exists(viewPath), $"Expected view file at {viewPath}");

        var doc = XDocument.Load(viewPath);
        XNamespace ns = "https://github.com/avaloniaui";

        var startupFilterPanel = doc
            .Descendants(ns + "WrapPanel")
            .FirstOrDefault(p => string.Equals((string?)p.Attribute("IsVisible"), "{Binding IsStartupTab}", StringComparison.Ordinal));

        Assert.NotNull(startupFilterPanel);

        // No vertical separator border inside the Startup filter WrapPanel
        Assert.Empty(startupFilterPanel!.Elements(ns + "Border"));

        // Exactly 3 state filter buttons: Усі стани, Увімкнено, Вимкнено
        var buttons = startupFilterPanel.Elements(ns + "Button").ToList();
        Assert.Equal(3, buttons.Count);
        Assert.Equal(
            ["Усі стани", "Увімкнено", "Вимкнено"],
            buttons.Select(static b => (string?)b.Attribute("Content") ?? string.Empty).ToArray());

        Assert.All(buttons, static b =>
            Assert.Equal("{Binding SetStartupStateFilterCommand}", (string?)b.Attribute("Command") ?? string.Empty));

        // Removed source filter command must not appear anywhere in the Startup view
        string rawXaml = File.ReadAllText(viewPath);
        Assert.DoesNotContain("SetStartupFilterCommand", rawXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsStartupSourceAll", rawXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsStartupSourceRegistry", rawXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsStartupSourceStartup", rawXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsStartupSourceTaskScheduler", rawXaml, StringComparison.Ordinal);
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

        throw new DirectoryNotFoundException("Could not locate repository root containing Argus.sln.");
    }

    private sealed class AllSourcesStartupManager : IStartupAndServicesManager
    {
        public Task<IReadOnlyList<StartupEntryItem>> GetStartupEntriesAsync(
            bool includeSystemTasks = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StartupEntryItem>>(
            [
                new("HKCU:App1", "ArgusTray", @"C:\Apps\Argus.exe", StartupSourceKind.RegistryCurrentUserRun, @"HKCU\Run", true, false, false, "User"),
                new("HKLM:App2", "AudioHelper", @"C:\Apps\Audio.exe", StartupSourceKind.RegistryLocalMachineRun, @"HKLM\Run", false, true, false, "Vendor"),
                new("WOW64:App3", "LegacyAgent", @"C:\Apps\Legacy.exe", StartupSourceKind.RegistryLocalMachineWow6432Run, @"HKLM\WOW64\Run", false, true, false, "Vendor"),
                new("FolderUser:App4", "NotesSync", @"C:\Users\Me\Startup\notes.lnk", StartupSourceKind.StartupFolderUser, @"Startup\notes.lnk", true, false, false, "User"),
                new("FolderCommon:App5", "SharedTool", @"C:\ProgramData\Startup\tool.lnk", StartupSourceKind.StartupFolderCommon, @"CommonStartup\tool.lnk", false, true, false, "All Users"),
                new("Task:App6", "NightlyBackup", @"C:\Tools\backup.exe", StartupSourceKind.TaskScheduler, @"\NightlyBackup", true, true, false, "Admin")
            ]);

        public Task<SystemActionResult> SetStartupEntryEnabledAsync(
            StartupEntryItem entry,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));

        public Task<IReadOnlyList<ManagedServiceItem>> GetManagedServicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedServiceItem>>([]);

        public Task<SystemActionResult> ControlServiceAsync(
            string serviceName,
            ServiceControlAction action,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));

        public Task<SystemActionResult> SetServiceStartModeAsync(
            string serviceName,
            ServiceStartModeOption startMode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SystemActionResult(true, false, "OK"));
    }
}
