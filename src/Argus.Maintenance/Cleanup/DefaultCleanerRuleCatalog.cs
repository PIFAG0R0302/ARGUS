using System.Text;
using System.Text.Json;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;

namespace Argus.Maintenance.Cleanup;

/// <summary>
/// Provisions default external JSON cleanup rule files into the rules folder if none exist,
/// and loads all external JSON rule files through ICleanerRuleLoader.
/// </summary>
public static class DefaultCleanerRuleCatalog
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = ArgusPaths.CreateJsonOptions();

    public static IReadOnlyList<CleanerRuleDefinition> GetBuiltInSystemRules() =>
    [
        new(
            Id: "win.user_temp",
            DisplayName: "Тимчасові файли користувача (%TEMP%)",
            Category: CleanerCategory.UserTemp,
            Description: "Тимчасові файли застосунків і розпаковані кеші старше 1 години в профілі користувача.",
            Paths: ["%TEMP%"],
            FilePatterns: ["*"],
            Recursive: true,
            MinFileAgeHours: 1,
            RequiresElevation: false,
            EnabledByDefault: true),
        new(
            Id: "win.system_temp",
            DisplayName: "Системні тимчасові файли Windows",
            Category: CleanerCategory.WindowsTemp,
            Description: @"Залишки системних інсталяторів та фонових служб у %WINDIR%\Temp старше 1 години.",
            Paths: [@"%WINDIR%\Temp"],
            FilePatterns: ["*"],
            Recursive: true,
            MinFileAgeHours: 1,
            RequiresElevation: true,
            EnabledByDefault: true),
        new(
            Id: "win.crash_dumps",
            DisplayName: "Дампи аварійного завершення та звіти WER",
            Category: CleanerCategory.CrashDumps,
            Description: "Локальні дампи збоїв програм (CrashDumps) та архіви звітів Windows Error Reporting.",
            Paths:
            [
                @"%LOCALAPPDATA%\CrashDumps",
                @"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive",
                @"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportQueue",
                @"%ProgramData%\Microsoft\Windows\WER\ReportArchive",
                @"%ProgramData%\Microsoft\Windows\WER\ReportQueue",
                @"%WINDIR%\Minidump"
            ],
            FilePatterns: ["*.dmp", "*.hdmp", "*.mdmp", "*.wer"],
            Recursive: true,
            MinFileAgeHours: 0,
            RequiresElevation: false,
            EnabledByDefault: true),
        new(
            Id: "win.system_logs",
            DisplayName: "Журнали обслуговування Windows (CBS / DISM)",
            Category: CleanerCategory.SystemLogs,
            Description: "Застарілі архівні журнали компонентного сховища CBS і DISM старше 24 годин.",
            Paths: [@"%WINDIR%\Logs\CBS", @"%WINDIR%\Logs\DISM", @"%WINDIR%\Logs\MoSetup"],
            FilePatterns: ["*.log", "*.cab"],
            Recursive: true,
            MinFileAgeHours: 24,
            RequiresElevation: true,
            EnabledByDefault: false),
        new(
            Id: "win.update_cache",
            DisplayName: "Кеш завантажень Windows Update",
            Category: CleanerCategory.WindowsUpdateCache,
            Description: @"Завантажені інсталяційні пакети оновлень у SoftwareDistribution\Download старше 24 годин.",
            Paths: [@"%WINDIR%\SoftwareDistribution\Download"],
            FilePatterns: ["*"],
            Recursive: true,
            MinFileAgeHours: 24,
            RequiresElevation: true,
            EnabledByDefault: false),
        new(
            Id: "win.thumbnails",
            DisplayName: "Кеш ескізів Провідника Windows",
            Category: CleanerCategory.Thumbnails,
            Description: "Файли кешу мініатюр зображень thumbcache_*.db у профілі користувача.",
            Paths: [@"%LOCALAPPDATA%\Microsoft\Windows\Explorer"],
            FilePatterns: ["thumbcache_*.db"],
            Recursive: false,
            MinFileAgeHours: 0,
            RequiresElevation: false,
            EnabledByDefault: false),
        new(
            Id: "win.recycle_bin",
            DisplayName: "Кошик Windows",
            Category: CleanerCategory.RecycleBin,
            Description: "Видалені користувачем файли в Кошику на всіх локальних дисках.",
            Paths: ["$RECYCLE.BIN"],
            FilePatterns: ["*"],
            Recursive: false,
            MinFileAgeHours: 0,
            RequiresElevation: false,
            EnabledByDefault: true)
    ];

    public static IReadOnlyList<CleanerRuleDefinition> GetBuiltInBrowserRules() =>
    [
        new(
            Id: "browser.chrome_cache",
            DisplayName: "Кеш браузера Google Chrome (без паролів і сесій)",
            Category: CleanerCategory.BrowserCache,
            Description: "Тимчасовий вебкеш, Code Cache та GPUCache профілю Google Chrome. Куки, історія та паролі не зачіпаються.",
            Paths:
            [
                @"%LOCALAPPDATA%\Google\Chrome\User Data\Default\Cache\Cache_Data",
                @"%LOCALAPPDATA%\Google\Chrome\User Data\Default\Code Cache",
                @"%LOCALAPPDATA%\Google\Chrome\User Data\Default\GPUCache",
                @"%LOCALAPPDATA%\Google\Chrome\User Data\ShaderCache"
            ],
            FilePatterns: ["*"],
            Recursive: true,
            MinFileAgeHours: 0,
            RequiresElevation: false,
            EnabledByDefault: true),
        new(
            Id: "browser.edge_cache",
            DisplayName: "Кеш браузера Microsoft Edge (без паролів і сесій)",
            Category: CleanerCategory.BrowserCache,
            Description: "Тимчасовий вебкеш, Code Cache та GPUCache профілю Microsoft Edge. Авторизація та паролі зберігаються.",
            Paths:
            [
                @"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Cache\Cache_Data",
                @"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Code Cache",
                @"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\GPUCache",
                @"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\ShaderCache"
            ],
            FilePatterns: ["*"],
            Recursive: true,
            MinFileAgeHours: 0,
            RequiresElevation: false,
            EnabledByDefault: true)
    ];

    public static async Task<CleanerRuleLoadResult> EnsureAndLoadRulesAsync(
        ICleanerRuleLoader ruleLoader,
        string? rulesDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruleLoader);

        string dir = string.IsNullOrWhiteSpace(rulesDirectory)
            ? ArgusPaths.GetDefaultCleanerRulesDirectory()
            : rulesDirectory;

        Directory.CreateDirectory(dir);

        if (!Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly).Any())
        {
            string sysFile = Path.Combine(dir, "default-windows-cleanup.json");
            string browserFile = Path.Combine(dir, "browsers-and-apps.json");

            string sysJson = JsonSerializer.Serialize(new { rules = GetBuiltInSystemRules() }, JsonOptions);
            string browserJson = JsonSerializer.Serialize(new { rules = GetBuiltInBrowserRules() }, JsonOptions);

            await File.WriteAllTextAsync(sysFile, sysJson, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(browserFile, browserJson, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }

        return await ruleLoader.LoadRulesAsync(dir, cancellationToken).ConfigureAwait(false);
    }
}
