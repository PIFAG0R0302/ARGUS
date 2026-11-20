using Argus.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Argus.Core.Tests;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusLogTests_{Guid.NewGuid():N}");

    public FileLoggerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void FileLogger_WritesFormattedEntriesAndPrunesStaleDailyLogs()
    {
        // Seed older daily log files beyond retention limit (3 days)
        File.WriteAllText(Path.Combine(_tempDir, "argus-20200101.log"), "old-1");
        File.WriteAllText(Path.Combine(_tempDir, "argus-20200102.log"), "old-2");
        File.WriteAllText(Path.Combine(_tempDir, "argus-20200103.log"), "old-3");
        File.WriteAllText(Path.Combine(_tempDir, "argus-20200104.log"), "old-4");

        using var provider = new ArgusFileLoggerProvider(_tempDir, LogLevel.Information, maxRetainedDays: 3);
        var logger = provider.CreateLogger("Argus.Test.Component");

        logger.LogDebug("Filtered out debug line");
        logger.LogInformation("Started service {Id}", 42);
        logger.LogError(new InvalidOperationException("sample-err"), "Failure detected");

        string currentLog = provider.GetCurrentLogFilePath();
        Assert.True(File.Exists(currentLog));

        string content = File.ReadAllText(currentLog);
        Assert.DoesNotContain("Filtered out debug line", content, StringComparison.Ordinal);
        Assert.Contains("[INF] [Component] Started service 42", content, StringComparison.Ordinal);
        Assert.Contains("[ERR] [Component] Failure detected", content, StringComparison.Ordinal);
        Assert.Contains("sample-err", content, StringComparison.Ordinal);

        string[] remaining = Directory.GetFiles(_tempDir, "argus-*.log");
        Assert.Equal(3, remaining.Length);
    }

    [Fact]
    public async Task FileLogger_HandlesConcurrentWritesSafely()
    {
        var ct = TestContext.Current.CancellationToken;
        using var provider = new ArgusFileLoggerProvider(_tempDir, LogLevel.Information, maxRetainedDays: 7);
        var logger = provider.CreateLogger("Argus.Concurrency");

        var tasks = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 25; i++)
            {
                logger.LogInformation("Worker {W} msg {I}", w, i);
            }
        }, ct));

        await Task.WhenAll(tasks);

        string[] lines = await File.ReadAllLinesAsync(provider.GetCurrentLogFilePath(), ct);
        Assert.Equal(100, lines.Length);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
