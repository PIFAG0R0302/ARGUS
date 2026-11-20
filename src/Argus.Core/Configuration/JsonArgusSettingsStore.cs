using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Argus.Core.Configuration;

public sealed class JsonArgusSettingsStore : IArgusSettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = ArgusPaths.CreateJsonOptions();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<JsonArgusSettingsStore> _logger;
    private ArgusSettings _current = new();

    public JsonArgusSettingsStore(ILogger<JsonArgusSettingsStore> logger, string? settingsFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        SettingsFilePath = string.IsNullOrWhiteSpace(settingsFilePath)
            ? ArgusPaths.GetDefaultSettingsPath()
            : settingsFilePath;
    }

    public string SettingsFilePath { get; }

    public ArgusSettings Current => _current;

    public async Task<ArgusSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                _current = new ArgusSettings().Normalize();
                return _current;
            }

            try
            {
                string json = await File.ReadAllTextAsync(SettingsFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<ArgusSettings>(json, JsonOptions);
                _current = (parsed ?? new ArgusSettings()).Normalize();
                return _current;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Corrupted or unreadable settings file at {Path}; reverting to defaults.", SettingsFilePath);
                TryBackupCorruptedFile();
                _current = new ArgusSettings().Normalize();
                return _current;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ArgusSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(normalized, JsonOptions);
            string tempPath = $"{SettingsFilePath}.{Guid.NewGuid():N}.tmp";

            try
            {
                await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
                    .ConfigureAwait(false);
                File.Move(tempPath, SettingsFilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* best effort cleanup */ }
                }
            }

            _current = normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryBackupCorruptedFile()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string backup = $"{SettingsFilePath}.corrupted.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                File.Move(SettingsFilePath, backup, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to back up corrupted settings file {Path}.", SettingsFilePath);
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
