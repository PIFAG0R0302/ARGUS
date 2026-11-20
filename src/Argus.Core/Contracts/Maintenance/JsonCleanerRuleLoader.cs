using System.Text;
using System.Text.Json;
using Argus.Core.Configuration;

namespace Argus.Core.Contracts.Maintenance;

/// <summary>
/// Loads and validates external JSON cleanup rule files while blocking dangerous system root paths.
/// </summary>
public sealed class JsonCleanerRuleLoader : ICleanerRuleLoader
{
    private static readonly JsonSerializerOptions JsonOptions = ArgusPaths.CreateJsonOptions();

    private static readonly HashSet<string> BlockedExactTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\",
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\Users",
        @"C:\ProgramData"
    };

    private static readonly string[] BlockedSubtreePrefixes =
    [
        @"C:\Windows\System32",
        @"C:\Windows\SysWOW64",
        @"C:\Windows\WinSxS",
        @"C:\Windows\Boot",
        @"C:\System Volume Information"
    ];

    private static readonly HashSet<string> BlockedRawTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "%SystemDrive%",
        "%SystemDrive%\\",
        "%SystemRoot%",
        "%SystemRoot%\\",
        "%WINDIR%",
        "%WINDIR%\\",
        "%USERPROFILE%",
        "%USERPROFILE%\\",
        "%ProgramFiles%",
        "%ProgramFiles(x86)%"
    };

    public async Task<CleanerRuleLoadResult> LoadRulesAsync(string rulesDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesDirectory);

        if (!Directory.Exists(rulesDirectory))
        {
            return new CleanerRuleLoadResult([], [$"Теку правил очищення не знайдено: {rulesDirectory}"]);
        }

        var validRules = new List<CleanerRuleDefinition>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        var files = Directory.EnumerateFiles(rulesDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(file);

            IReadOnlyList<CleanerRuleDefinition>? parsed;
            try
            {
                string raw = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                parsed = ParseRuleFile(raw);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                warnings.Add($"Файл правил '{fileName}' пропущено через помилку читання JSON: {ex.Message}");
                continue;
            }

            foreach (var rule in parsed)
            {
                if (ValidateRule(rule, fileName, seenIds, warnings, out var normalized))
                {
                    validRules.Add(normalized!);
                }
            }
        }

        return new CleanerRuleLoadResult(validRules, warnings);
    }

    private static List<CleanerRuleDefinition> ParseRuleFile(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<CleanerRuleDefinition>>(rawJson, JsonOptions) ?? [];
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("rules", out var rulesElement) &&
            rulesElement.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<CleanerRuleDefinition>>(rulesElement.GetRawText(), JsonOptions) ?? [];
        }

        var single = JsonSerializer.Deserialize<CleanerRuleDefinition>(rawJson, JsonOptions);
        return single is null ? [] : [single];
    }

    private static bool ValidateRule(
        CleanerRuleDefinition? rule,
        string fileName,
        HashSet<string> seenIds,
        List<string> warnings,
        out CleanerRuleDefinition? normalized)
    {
        normalized = null;
        if (rule is null)
        {
            warnings.Add($"Порожній запис правила у '{fileName}'.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.DisplayName))
        {
            warnings.Add($"Правило у '{fileName}' не має обов'язкового Id або DisplayName.");
            return false;
        }

        string trimmedId = rule.Id.Trim();
        if (!seenIds.Add(trimmedId))
        {
            warnings.Add($"Дубльований Id правила '{trimmedId}' у '{fileName}' — пропущено.");
            return false;
        }

        if (rule.Paths is null || rule.Paths.Count == 0)
        {
            warnings.Add($"Правило '{trimmedId}' не містить жодного шляху.");
            return false;
        }

        var safePaths = new List<string>();
        foreach (string rawPath in rule.Paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string candidate = rawPath.Trim();
            if (IsDangerousSystemPath(candidate))
            {
                warnings.Add($"Заблоковано небезпечний системний шлях '{candidate}' у правилі '{trimmedId}'.");
                continue;
            }

            safePaths.Add(candidate);
        }

        if (safePaths.Count == 0)
        {
            warnings.Add($"Правило '{trimmedId}' відхилено: не залишилося безпечних шляхів.");
            return false;
        }

        var patterns = rule.FilePatterns is { Count: > 0 }
            ? rule.FilePatterns.Where(static p => !string.IsNullOrWhiteSpace(p)).Select(static p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : ["*"];

        normalized = rule with
        {
            Id = trimmedId,
            DisplayName = rule.DisplayName.Trim(),
            Description = rule.Description?.Trim() ?? string.Empty,
            Paths = safePaths,
            FilePatterns = patterns,
            MinFileAgeHours = Math.Max(0, rule.MinFileAgeHours)
        };
        return true;
    }

    public static bool IsDangerousSystemPath(string path)
    {
        string trimmed = path.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            return true;
        }

        // Block UNC and raw device namespace paths (\\server\share, \\?\..., \\.\...)
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        string normalizedRaw = NormalizeSeparators(trimmed);
        if (BlockedRawTokens.Contains(normalizedRaw))
        {
            return true;
        }

        string expanded = NormalizeSeparators(Environment.ExpandEnvironmentVariables(normalizedRaw)).TrimEnd('\\');
        if (expanded.Length <= 2 && expanded.EndsWith(':'))
        {
            return true;
        }

        string withSlash = expanded + "\\";
        if (BlockedExactTargets.Contains(expanded) || BlockedExactTargets.Contains(withSlash))
        {
            return true;
        }

        foreach (string prefix in BlockedSubtreePrefixes)
        {
            if (expanded.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                expanded.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeSeparators(string value)
    {
        string replaced = value.Replace('/', '\\');
        while (replaced.Contains(@"\\", StringComparison.Ordinal))
        {
            replaced = replaced.Replace(@"\\", @"\", StringComparison.Ordinal);
        }

        return replaced;
    }
}
