using Argus.Core.Collections;
using Xunit;

namespace Argus.Core.Tests;

public sealed class RingBufferTests
{
    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(-5));
    }

    [Fact]
    public void AddAndSnapshot_PreservesChronologicalOrderAfterWrapAround()
    {
        var buffer = new RingBuffer<int>(3);
        Assert.False(buffer.TryGetLatest(out _));
        Assert.Empty(buffer.Snapshot());

        buffer.Add(10);
        buffer.Add(20);
        buffer.Add(30);
        buffer.Add(40);
        buffer.Add(50);

        Assert.Equal(3, buffer.Count);
        Assert.True(buffer.TryGetLatest(out int latest));
        Assert.Equal(50, latest);
        Assert.Equal([30, 40, 50], buffer.Snapshot());
    }

    [Fact]
    public void Clear_ResetsBufferState()
    {
        var buffer = new RingBuffer<string>(4);
        buffer.Add("a");
        buffer.Add("b");
        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.False(buffer.TryGetLatest(out _));
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public async Task ConcurrentWritesAndSnapshots_DoNotCorruptOrThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var buffer = new RingBuffer<int>(64);

        var writers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                buffer.Add(worker * 1000 + i);
            }
        }, ct));

        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                var snap = buffer.Snapshot();
                Assert.True(snap.Length <= 64);
            }
        }, ct));

        await Task.WhenAll(writers.Concat(readers));
        Assert.Equal(64, buffer.Count);
    }
}
