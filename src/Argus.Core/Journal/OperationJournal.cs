using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Argus.Core.Collections;
using Argus.Core.Configuration;
using Argus.Core.Events;
using Microsoft.Extensions.Logging;

namespace Argus.Core.Journal;

public enum OperationSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record OperationJournalEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string ModuleId,
    OperationSeverity Severity,
    string Action,
    string Details,
    bool Succeeded);

public sealed record OperationJournalEntryAddedEvent(OperationJournalEntry Entry);

public interface IOperationJournal
{
    Task RecordAsync(
        string moduleId,
        OperationSeverity severity,
        string action,
        string details,
        bool succeeded = true,
        CancellationToken cancellationToken = default);

    IReadOnlyList<OperationJournalEntry> GetRecent(int maxCount = 200);
}

public sealed class FileBackedOperationJournal : IOperationJournal, IDisposable
{
    private static readonly JsonSerializerOptions LineJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly RingBuffer<OperationJournalEntry> _recent;
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly IArgusEventBus _eventBus;
    private readonly ILogger<FileBackedOperationJournal> _logger;
    private readonly string? _journalFilePath;

    public FileBackedOperationJournal(
        IArgusEventBus eventBus,
        ILogger<FileBackedOperationJournal> logger,
        string? journalFilePath = null,
        int memoryCapacity = 1000)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(memoryCapacity, 0);

        _eventBus = eventBus;
        _logger = logger;
        _journalFilePath = journalFilePath;
        _recent = new RingBuffer<OperationJournalEntry>(memoryCapacity);
    }

    public async Task RecordAsync(
        string moduleId,
        OperationSeverity severity,
        string action,
        string details,
        bool succeeded = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var entry = new OperationJournalEntry(
            Id: Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            ModuleId: moduleId.Trim(),
            Severity: severity,
            Action: action.Trim(),
            Details: details ?? string.Empty,
            Succeeded: succeeded);

        _recent.Add(entry);

        if (!string.IsNullOrWhiteSpace(_journalFilePath))
        {
            await AppendToFileAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        _eventBus.Publish(new OperationJournalEntryAddedEvent(entry));
    }

    public IReadOnlyList<OperationJournalEntry> GetRecent(int maxCount = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxCount, 0);

        var all = _recent.Snapshot();
        if (all.Length <= maxCount)
        {
            return all;
        }

        var slice = new OperationJournalEntry[maxCount];
        Array.Copy(all, all.Length - maxCount, slice, 0, maxCount);
        return slice;
    }

    private async Task AppendToFileAsync(OperationJournalEntry entry, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(_journalFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string line = JsonSerializer.Serialize(entry, LineJsonOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(_journalFilePath!, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to persist journal entry to {Path}.", _journalFilePath);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public void Dispose()
    {
        _fileGate.Dispose();
    }
}
