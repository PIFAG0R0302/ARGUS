using Argus.Core.Configuration;
using Argus.Core.Events;
using Argus.Core.Journal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.Core.Tests;

public sealed class SettingsAndJournalTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusCoreTests_{Guid.NewGuid():N}");

    public SettingsAndJournalTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task SettingsStore_ReturnsDefaultsWhenMissingAndRoundTripsNormalizedValues()
    {
        var ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(_tempDir, "settings.json");
        using var store = new JsonArgusSettingsStore(NullLogger<JsonArgusSettingsStore>.Instance, path);

        var initial = await store.LoadAsync(ct);
        Assert.Equal(1000, initial.TelemetryIntervalMs);
        Assert.False(initial.EnablePawnIoExtendedSensors);
        Assert.True(initial.CreateRestorePointBeforeCleanup);

        var custom = new ArgusSettings
        {
            TelemetryIntervalMs = 10, // clamped to 250
            HistoryWindowSeconds = 99_999, // clamped to 3600
            CpuTemperatureAlertCelsius = 200.0, // clamped to 115
            EnablePawnIoExtendedSensors = true,
            CleanerExclusions = [" C:\\Keep ", "c:\\keep", ""]
        };

        await store.SaveAsync(custom, ct);
        var reloaded = await store.LoadAsync(ct);

        Assert.Equal(250, reloaded.TelemetryIntervalMs);
        Assert.Equal(3600, reloaded.HistoryWindowSeconds);
        Assert.Equal(115.0, reloaded.CpuTemperatureAlertCelsius);
        Assert.True(reloaded.EnablePawnIoExtendedSensors);
        Assert.Equal(["C:\\Keep"], reloaded.CleanerExclusions);
    }

    [Fact]
    public async Task SettingsStore_RecoversFromCorruptedJsonAndCreatesBackup()
    {
        var ct = TestContext.Current.CancellationToken;
        string path = Path.Combine(_tempDir, "broken.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json", ct);

        using var store = new JsonArgusSettingsStore(NullLogger<JsonArgusSettingsStore>.Instance, path);
        var loaded = await store.LoadAsync(ct);

        Assert.Equal(1000, loaded.TelemetryIntervalMs);
        var backups = Directory.GetFiles(_tempDir, "broken.json.corrupted.*");
        Assert.Single(backups);
    }

    [Fact]
    public void ArgusPaths_SwitchesToPortableDataDirectoryWhenFlagExists()
    {
        string portableRoot = Path.Combine(_tempDir, "portable-app");
        Directory.CreateDirectory(portableRoot);
        File.WriteAllText(Path.Combine(portableRoot, "portable.flag"), string.Empty);

        string dataDir = ArgusPaths.GetDataDirectory(portableRoot);
        Assert.Equal(Path.Combine(portableRoot, "data"), dataDir);
    }

    [Fact]
    public async Task OperationJournal_RecordsPersistsAndPublishesEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        string journalFile = Path.Combine(_tempDir, "logs", "journal.jsonl");
        var bus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        OperationJournalEntry? received = null;

        using var sub = bus.Subscribe<OperationJournalEntryAddedEvent>(e => received = e.Entry);
        using var journal = new FileBackedOperationJournal(
            bus,
            NullLogger<FileBackedOperationJournal>.Instance,
            journalFile,
            memoryCapacity: 3);

        for (int i = 1; i <= 5; i++)
        {
            await journal.RecordAsync("argus.test", OperationSeverity.Info, $"Action {i}", $"Details {i}", cancellationToken: ct);
        }

        var recent = journal.GetRecent(10);
        Assert.Equal(3, recent.Count);
        Assert.Equal("Action 3", recent[0].Action);
        Assert.Equal("Action 5", recent[^1].Action);
        Assert.NotNull(received);
        Assert.Equal("Action 5", received!.Action);

        string[] lines = await File.ReadAllLinesAsync(journalFile, ct);
        Assert.Equal(5, lines.Length);
        Assert.Contains("\"severity\":\"Info\"", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ArgusSettings_Normalize_MigratesLegacySourceRepoToPublicUpdatesRepo()
    {
        var legacy = new ArgusSettings { GitHubUpdateRepository = "pifag0r0302/argus" }.Normalize();
        Assert.Equal(ArgusSettings.DefaultGitHubRepository, legacy.GitHubUpdateRepository);

        var custom = new ArgusSettings { GitHubUpdateRepository = "custom-org/argus-mirror" }.Normalize();
        Assert.Equal("custom-org/argus-mirror", custom.GitHubUpdateRepository);
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
