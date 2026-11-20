namespace Argus.Core.Collections;

/// <summary>
/// Thread-safe, fixed-capacity circular buffer for bounded time-series data.
/// </summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _buffer;
    private readonly Lock _sync = new();
    private int _head;
    private int _count;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        _buffer = new T[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public void Add(T item)
    {
        lock (_sync)
        {
            _buffer[_head] = item;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length)
            {
                _count++;
            }
        }
    }

    public bool TryGetLatest(out T? value)
    {
        lock (_sync)
        {
            if (_count == 0)
            {
                value = default;
                return false;
            }

            int lastIndex = (_head - 1 + _buffer.Length) % _buffer.Length;
            value = _buffer[lastIndex];
            return true;
        }
    }

    /// <summary>
    /// Returns items in chronological order (oldest first, newest last).
    /// </summary>
    public T[] Snapshot() => SnapshotTail(int.MaxValue);

    /// <summary>
    /// Returns up to the most recent <paramref name="maxCount"/> items in chronological order without copying the entire buffer.
    /// </summary>
    public T[] SnapshotTail(int maxCount)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        lock (_sync)
        {
            if (_count == 0)
            {
                return [];
            }

            int take = Math.Min(maxCount, _count);
            var result = new T[take];
            int start = (_head - take + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < take; i++)
            {
                result[i] = _buffer[(start + i) % _buffer.Length];
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _count = 0;
        }
    }
}
