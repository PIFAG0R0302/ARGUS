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

        CancellationToken ct = TestContext.Current.CancellationToken;
        using var primaryAcquired = new ManualResetEventSlim(false);

        // Mutex has thread affinity: acquire and release on the same dedicated thread.
        var primaryThread = new Thread(() =>
        {
            using var primary = new SingleInstanceGuard(mutexName, pipeName);
            if (primary.TryAcquire())
            {
                primaryAcquired.Set();
                Thread.Sleep(200);
            }
        })
        {
            IsBackground = true
        };
        primaryThread.Start();
        Assert.True(primaryAcquired.Wait(TimeSpan.FromSeconds(2), ct));

        using var elevated = new SingleInstanceGuard(mutexName, pipeName);
        bool acquired = await Task.Run(() =>
            elevated.TryAcquire(waitForPreviousExit: true, handoffTimeout: TimeSpan.FromSeconds(3)), ct);

        primaryThread.Join(TimeSpan.FromSeconds(2));

        Assert.True(acquired);
        Assert.True(elevated.IsPrimaryInstance);
    }
}
