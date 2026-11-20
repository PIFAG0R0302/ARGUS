using Argus.Core.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Core.Tests;

public sealed class ArgusEventBusTests
{
    private sealed record TestPingEvent(string Payload);

    [Fact]
    public void Publish_DeliversToActiveSubscribersAndStopsAfterDispose()
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        var received = new List<string>();

        using var sub = bus.Subscribe<TestPingEvent>(e => received.Add(e.Payload));
        bus.Publish(new TestPingEvent("first"));

        sub.Dispose();
        sub.Dispose(); // idempotent

        bus.Publish(new TestPingEvent("second"));

        Assert.Equal(["first"], received);
    }

    [Fact]
    public void Publish_IsolatesSubscriberExceptions()
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        var secondCalled = false;

        using var badSub = bus.Subscribe<TestPingEvent>(_ => throw new InvalidOperationException("boom"));
        using var goodSub = bus.Subscribe<TestPingEvent>(_ => secondCalled = true);

        bus.Publish(new TestPingEvent("probe"));

        Assert.True(secondCalled);
    }

    [Fact]
    public void Publish_AllowsSubscriberToUnsubscribeDuringCallback()
    {
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        IDisposable? self = null;
        int callCount = 0;

        self = bus.Subscribe<TestPingEvent>(_ =>
        {
            callCount++;
            self?.Dispose();
        });

        bus.Publish(new TestPingEvent("one"));
        bus.Publish(new TestPingEvent("two"));

        Assert.Equal(1, callCount);
    }
}
