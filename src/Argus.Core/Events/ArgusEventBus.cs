using Microsoft.Extensions.Logging;

namespace Argus.Core.Events;

/// <summary>
/// Thread-safe, fault-isolated in-process publish/subscribe bus.
/// </summary>
public sealed class ArgusEventBus : IArgusEventBus
{
    private readonly Lock _sync = new();
    private readonly Dictionary<Type, List<Delegate>> _subscribers = [];
    private readonly ILogger<ArgusEventBus> _logger;

    public ArgusEventBus(ILogger<ArgusEventBus> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);
        var eventType = typeof(TEvent);

        lock (_sync)
        {
            if (!_subscribers.TryGetValue(eventType, out var list))
            {
                list = [];
                _subscribers[eventType] = list;
            }

            list.Add(handler);
        }

        return new Subscription(this, eventType, handler);
    }

    public void Publish<TEvent>(TEvent message) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(message);

        Delegate[] snapshot;
        lock (_sync)
        {
            if (!_subscribers.TryGetValue(typeof(TEvent), out var list) || list.Count == 0)
            {
                return;
            }

            snapshot = [.. list];
        }

        foreach (var entry in snapshot)
        {
            try
            {
                ((Action<TEvent>)entry)(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event subscriber failed while handling {EventType}", typeof(TEvent).Name);
            }
        }
    }

    private void Unsubscribe(Type eventType, Delegate handler)
    {
        lock (_sync)
        {
            if (!_subscribers.TryGetValue(eventType, out var list))
            {
                return;
            }

            list.Remove(handler);
            if (list.Count == 0)
            {
                _subscribers.Remove(eventType);
            }
        }
    }

    private sealed class Subscription(ArgusEventBus owner, Type eventType, Delegate handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(eventType, handler);
            }
        }
    }
}
