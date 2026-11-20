using System.Text.Json;
using System.Text.Json.Serialization;

namespace Argus.Core.Configuration;

public sealed record ArgusSettings
{
    public const string DefaultTrustedSignerThumbprint = "9D1B22482AC32CEA02F398527F9409FCA09EA78D";
    public const string DefaultGitHubRepository = "PIFAG0R0302/ARGUS";

    public int TelemetryIntervalMs { get; init; } = 1000;
    public int HistoryWindowSeconds { get; init; } = 180;
    public bool EnablePawnIoExtendedSensors { get; init; }
    public double CpuTemperatureAlertCelsius { get; init; } = 85.0;
    public double GpuTemperatureAlertCelsius { get; init; } = 85.0;
    public double RamUsageAlertPercent { get; init; } = 90.0;
    public bool CreateRestorePointBeforeCleanup { get; init; } = true;
    public bool MinimizeToTrayOnClose { get; init; }
    public bool StartWithWindows { get; init; }
    public bool CheckUpdatesOnStartup { get; init; } = true;
    public string GitHubUpdateRepository { get; init; } = DefaultGitHubRepository;
    public string TrustedSignerThumbprint { get; init; } = DefaultTrustedSignerThumbprint;
    public string Language { get; init; } = "uk-UA";
    public IReadOnlyList<string> CleanerExclusions { get; init; } = [];

    public ArgusSettings Normalize() => this with
    {
        TelemetryIntervalMs = Math.Clamp(TelemetryIntervalMs, 250, 10_000),
        HistoryWindowSeconds = Math.Clamp(HistoryWindowSeconds, 30, 3_600),
        CpuTemperatureAlertCelsius = Math.Clamp(CpuTemperatureAlertCelsius, 50.0, 115.0),
        GpuTemperatureAlertCelsius = Math.Clamp(GpuTemperatureAlertCelsius, 50.0, 115.0),
        RamUsageAlertPercent = Math.Clamp(RamUsageAlertPercent, 50.0, 99.0),
        GitHubUpdateRepository = string.IsNullOrWhiteSpace(GitHubUpdateRepository) || !GitHubUpdateRepository.Contains('/', StringComparison.Ordinal)
            ? DefaultGitHubRepository
            : GitHubUpdateRepository.Trim(),
        TrustedSignerThumbprint = string.IsNullOrWhiteSpace(TrustedSignerThumbprint)
            ? DefaultTrustedSignerThumbprint
            : TrustedSignerThumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant(),
        Language = string.IsNullOrWhiteSpace(Language) ? "uk-UA" : Language.Trim(),
        CleanerExclusions = CleanerExclusions is null
            ? []
            : CleanerExclusions
                .Where(static p => !string.IsNullOrWhiteSpace(p))
                .Select(static p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
    };
}

public sealed record SettingsChangedEvent(ArgusSettings Settings);

public interface IArgusSettingsStore
{
    string SettingsFilePath { get; }
    ArgusSettings Current { get; }
    Task<ArgusSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ArgusSettings settings, CancellationToken cancellationToken = default);
}

public static class ArgusPaths
{
    public static string GetDataDirectory(string? appBaseDirectory = null)
    {
        string baseDir = string.IsNullOrWhiteSpace(appBaseDirectory)
            ? AppContext.BaseDirectory
            : appBaseDirectory;

        string portableMarker = Path.Combine(baseDir, "portable.flag");
        if (File.Exists(portableMarker))
        {
            return Path.Combine(baseDir, "data");
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Argus");
    }

    public static string GetDefaultSettingsPath(string? appBaseDirectory = null) =>
        Path.Combine(GetDataDirectory(appBaseDirectory), "settings.json");

    public static string GetDefaultLogsDirectory(string? appBaseDirectory = null) =>
        Path.Combine(GetDataDirectory(appBaseDirectory), "logs");

    public static string GetDefaultJournalPath(string? appBaseDirectory = null) =>
        Path.Combine(GetDefaultLogsDirectory(appBaseDirectory), "journal.jsonl");

    public static string GetDefaultCleanerRulesDirectory(string? appBaseDirectory = null) =>
        Path.Combine(GetDataDirectory(appBaseDirectory), "rules");

    public static string GetDefaultUpdatesDirectory(string? appBaseDirectory = null) =>
        Path.Combine(GetDataDirectory(appBaseDirectory), "updates");

    public static JsonSerializerOptions CreateJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
