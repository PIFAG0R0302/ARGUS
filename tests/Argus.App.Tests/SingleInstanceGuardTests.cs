using Argus.App.Services;
using Xunit;

namespace Argus.App.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public async Task SecondaryInstance_CannotAcquire_AndSignalsPrimaryViaNamedPipe()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string mutexName = $@"Local\Argus_Test_SingleInstance_{suffix}";
        string pipeName = $"Argus_Test_Pipe_{suffix}";

        using var primary = new SingleInstanceGuard(mutexName, pipeName);
        Assert.True(primary.TryAcquire());
        Assert.True(primary.IsPrimaryInstance);

        var activatedTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartActivationListener(section => activatedTcs.TrySetResult(section));

        using var secondary = new SingleInstanceGuard(mutexName, pipeName);
        Assert.False(secondary.TryAcquire());
        Assert.False(secondary.IsPrimaryInstance);

        bool signaled = secondary.SignalExistingInstance("Monitoring", timeoutMs: 2000);
        Assert.True(signaled);

        string? receivedSection = await activatedTcs.Task.WaitAsync(
            TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);
        Assert.Equal("Monitoring", receivedSection);
    }

    [Fact]
    public async Task ElevationHandoff_WaitsForPreviousInstanceToReleaseMutex()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string mutexName = $@"Local\Argus_Test_Handoff_{suffix}";
        string pipeName = $"Argus_Test_HandoffPipe_{suffix}";

        var primary = new SingleInstanceGuard(mutexName, pipeName);
        Assert.True(primary.TryAcquire());

        CancellationToken ct = TestContext.Current.CancellationToken;

        // Release primary mutex shortly after handoff starts
        _ = Task.Run(async () =>
        {
            await Task.Delay(200, ct);
            primary.Dispose();
        }, ct);

        using var elevated = new SingleInstanceGuard(mutexName, pipeName);
        bool acquired = await Task.Run(() =>
            elevated.TryAcquire(waitForPreviousExit: true, handoffTimeout: TimeSpan.FromSeconds(3)), ct);

        Assert.True(acquired);
        Assert.True(elevated.IsPrimaryInstance);
    }
}
