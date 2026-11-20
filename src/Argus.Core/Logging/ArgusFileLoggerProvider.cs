using System.Globalization;
using System.Text;
using Argus.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Argus.Core.Logging;

/// <summary>
/// Thread-safe daily rotating UTF-8 file logger with automatic retention cleanup.
/// </summary>
public sealed class ArgusFileLoggerProvider : ILoggerProvider
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Lock _writeLock = new();
    private readonly string _logsDirectory;
    private readonly LogLevel _minLevel;
    private readonly int _maxRetainedDays;
    private string? _lastCleanedDate;
    private bool _disposed;

    public ArgusFileLoggerProvider(
        string? logsDirectory = null,
        LogLevel minLevel = LogLevel.Information,
        int maxRetainedDays = 7)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxRetainedDays, 0);
        _logsDirectory = string.IsNullOrWhiteSpace(logsDirectory)
            ? ArgusPaths.GetDefaultLogsDirectory()
            : logsDirectory;
        _minLevel = minLevel;
        _maxRetainedDays = maxRetainedDays;
    }

    public string LogsDirectory => _logsDirectory;

    public string GetCurrentLogFilePath() => GetLogFilePath(DateTimeOffset.Now);

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(this, categoryName, _minLevel);

    internal void WriteEntry(LogLevel level, string category, string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var sb = new StringBuilder(160);
        sb.Append('[').Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("] ");
        sb.Append('[').Append(FormatLevel(level)).Append("] ");
        sb.Append('[').Append(ShortCategory(category)).Append("] ");
        sb.Append(message);
        if (exception is not null)
        {
            sb.AppendLine();
            sb.Append(exception);
        }

        sb.AppendLine();
        string payload = sb.ToString();

        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_logsDirectory);
                string filePath = GetLogFilePath(now);
                File.AppendAllText(filePath, payload, Utf8NoBom);

                string dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (!string.Equals(_lastCleanedDate, dateStamp, StringComparison.Ordinal))
                {
                    PruneOldLogs();
                    _lastCleanedDate = dateStamp;
                }
            }
            catch
            {
                // Logging must never crash the host application.
            }
        }
    }

    private string GetLogFilePath(DateTimeOffset timestamp) =>
        Path.Combine(_logsDirectory, $"argus-{timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    private void PruneOldLogs()
    {
        try
        {
            var files = Directory.EnumerateFiles(_logsDirectory, "argus-*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(static f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .Skip(_maxRetainedDays);

            foreach (string stale in files)
            {
                try { File.Delete(stale); } catch { /* best effort */ }
            }
        }
        catch
        {
            // ignore enumeration errors
        }
    }

    private static string FormatLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "INF"
    };

    private static string ShortCategory(string category)
    {
        int lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1
            ? category[(lastDot + 1)..]
            : category;
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _disposed = true;
        }
    }

    private sealed class FileLogger(ArgusFileLoggerProvider provider, string category, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string text = formatter(state, exception);
            provider.WriteEntry(logLevel, category, text, exception);
        }
    }
}

public static class ArgusLoggingBuilderExtensions
{
    public static ILoggingBuilder AddArgusFileLogging(
        this ILoggingBuilder builder,
        string? logsDirectory = null,
        LogLevel minLevel = LogLevel.Information,
        int maxRetainedDays = 7)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<ILoggerProvider>(_ => new ArgusFileLoggerProvider(logsDirectory, minLevel, maxRetainedDays));
        return builder;
    }
}
