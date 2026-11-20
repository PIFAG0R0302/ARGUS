using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Argus.Core.Journal;
using Argus.Maintenance.SystemServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Xunit;

namespace Argus.App.Tests;

public sealed class SystemServicesTests : IDisposable
{
    private readonly string _testRegSubKey = $@"Software\ArgusUnitTests_{Guid.NewGuid():N}";

    [Fact]
    public void StartupRegistrationService_RoundTripsRegistryEntryInIsolatedKey()
    {
        var service = new WindowsStartupRegistrationService(_testRegSubKey, "ArgusTestValue");

        Assert.False(service.IsEnabled());

        service.SetEnabled(true, @"C:\Tools\Argus\Argus.exe");
        Assert.True(service.IsEnabled());

        using (var key = Registry.CurrentUser.OpenSubKey(_testRegSubKey, writable: false))
        {
            string? stored = key?.GetValue("ArgusTestValue") as string;
            Assert.Equal("\"C:\\Tools\\Argus\\Argus.exe\" --minimized", stored);
        }

        service.SetEnabled(false, string.Empty);
        Assert.False(service.IsEnabled());
    }

    [Fact]
    public async Task RestorePointService_BlocksWhenNotAdministratorWithoutCallingWmi()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(bus, NullLogger<FileBackedOperationJournal>.Instance);
        bool wmiCalled = false;

        var service = new WindowsRestorePointService(
            new StubPrivilege(isAdmin: false),
            journal,
            NullLogger<WindowsRestorePointService>.Instance,
            _ => { wmiCalled = true; return 0u; });

        var result = await service.CreateRestorePointAsync("Test Checkpoint", ct);

        Assert.False(result.Succeeded);
        Assert.False(wmiCalled);
        Assert.Contains("адміністратора", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(journal.GetRecent(), static e => e.Action == "RestorePointSkipped");
    }

    [Fact]
    public async Task RestorePointService_ReportsSuccessAndHandlesWmiErrorCodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var journal = new FileBackedOperationJournal(bus, NullLogger<FileBackedOperationJournal>.Instance);

        var okService = new WindowsRestorePointService(
            new StubPrivilege(isAdmin: true),
            journal,
            NullLogger<WindowsRestorePointService>.Instance,
            _ => 0u);

        var ok = await okService.CreateRestorePointAsync("Pre-Cleanup", ct);
        Assert.True(ok.Succeeded);
        Assert.Contains(journal.GetRecent(), static e => e.Action == "RestorePointCreated");

        var disabledService = new WindowsRestorePointService(
            new StubPrivilege(isAdmin: true),
            journal,
            NullLogger<WindowsRestorePointService>.Instance,
            _ => 1058u);

        var disabled = await disabledService.CreateRestorePointAsync("Pre-Cleanup", ct);
        Assert.False(disabled.Succeeded);
        Assert.Contains("вимкнена", disabled.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(journal.GetRecent(), static e => e.Action == "RestorePointFailed");
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_testRegSubKey, throwOnMissingSubKey: false);
        }
        catch
        {
            // best-effort registry cleanup
        }
    }

    private sealed class StubPrivilege(bool isAdmin) : IPrivilegeService
    {
        public bool IsAdministrator { get; } = isAdmin;
        public bool TryRelaunchElevated(string[]? arguments = null) => false;
    }
}
