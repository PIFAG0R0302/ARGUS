using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Telemetry;
using Argus.Core.Journal;

namespace Argus.Telemetry.Reporting;

/// <summary>
/// Exports live hardware telemetry snapshots and workstation summary in self-contained HTML, JSON, and CSV formats.
/// Writes UTF-8 without BOM for strict interoperability.
/// </summary>
public sealed class TelemetryReportExporter : ITelemetryReportExporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = ArgusPaths.CreateJsonOptions();

    private readonly ITelemetryService _telemetryService;
    private readonly IArgusSettingsStore _settingsStore;
    private readonly IOperationJournal _journal;

    public TelemetryReportExporter(
        ITelemetryService telemetryService,
        IArgusSettingsStore settingsStore,
        IOperationJournal journal)
    {
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public async Task<TelemetryReportExportResult> ExportAsync(
        TelemetryReportFormat format,
        string? outputDirectory = null,
        int maxSamples = 360,
        CancellationToken cancellationToken = default)
    {
        string? settingsDir = Path.GetDirectoryName(_settingsStore.SettingsFilePath);
        string defaultBaseDir = string.IsNullOrWhiteSpace(settingsDir)
            ? ArgusPaths.GetDataDirectory()
            : settingsDir;

        string dir = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(defaultBaseDir, "reports")
            : outputDirectory;

        Directory.CreateDirectory(dir);

        var history = _telemetryService.GetHistory(Math.Max(1, maxSamples));
        IReadOnlyList<TelemetrySnapshot> samples = history.Count > 0
            ? history
            : (_telemetryService.Latest is { } latest ? [latest] : []);

        string ext = format switch
        {
            TelemetryReportFormat.Html => "html",
            TelemetryReportFormat.Json => "json",
            TelemetryReportFormat.Csv => "csv",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Непідтримуваний формат звіту.")
        };

        string baseName = $"argus-telemetry-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        string filePath = Path.Combine(dir, $"{baseName}.{ext}");
        int suffix = 1;
        while (File.Exists(filePath))
        {
            filePath = Path.Combine(dir, $"{baseName}-{suffix}.{ext}");
            suffix++;
        }

        try
        {
            string content = format switch
            {
                TelemetryReportFormat.Json => BuildJsonReport(samples),
                TelemetryReportFormat.Csv => BuildCsvReport(samples),
                TelemetryReportFormat.Html => BuildHtmlReport(samples),
                _ => string.Empty
            };

            await File.WriteAllTextAsync(filePath, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);

            await _journal.RecordAsync(
                moduleId: "argus.telemetry",
                severity: OperationSeverity.Success,
                action: "TelemetryReportExported",
                details: $"Формат={format}, зразків={samples.Count}, файл={filePath}",
                succeeded: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new TelemetryReportExportResult(
                Succeeded: true,
                Format: format,
                FilePath: filePath,
                SampleCount: samples.Count,
                Message: $"Експортовано ({format}, зразків: {samples.Count}): {filePath}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _journal.RecordAsync(
                moduleId: "argus.telemetry",
                severity: OperationSeverity.Error,
                action: "TelemetryReportExportFailed",
                details: $"Формат={format}, помилка={ex.Message}",
                succeeded: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new TelemetryReportExportResult(
                Succeeded: false,
                Format: format,
                FilePath: filePath,
                SampleCount: samples.Count,
                Message: $"Помилка експорту {format}: {ex.Message}");
        }
    }

    private string BuildJsonReport(IReadOnlyList<TelemetrySnapshot> samples)
    {
        var payload = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            osDescription = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            settings = _settingsStore.Current,
            latest = samples.Count > 0 ? samples[^1] : null,
            history = samples
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string BuildCsvReport(IReadOnlyList<TelemetrySnapshot> samples)
    {
        var sb = new StringBuilder(samples.Count * 128 + 160);
        sb.AppendLine("TimestampUtc,SensorTier,CpuLoadPercent,CpuClockMHz,CpuTempC,RamUsedGiB,RamTotalGiB,RamLoadPercent,PrimaryGpuName,GpuLoadPercent,GpuVramUsedGiB,GpuTempC,PeakDiskTempC");

        const double gib = 1024.0 * 1024.0 * 1024.0;
        foreach (var snap in samples)
        {
            var gpu = snap.Gpus.FirstOrDefault();
            double? peakDiskTemp = snap.Storage
                .Where(static d => d.TemperatureCelsius.HasValue)
                .Select(static d => d.TemperatureCelsius)
                .Max();

            string ramUsedGiB = snap.Memory.TotalBytes > 0
                ? (snap.Memory.UsedBytes / gib).ToString("0.00", CultureInfo.InvariantCulture)
                : string.Empty;
            string ramTotalGiB = snap.Memory.TotalBytes > 0
                ? (snap.Memory.TotalBytes / gib).ToString("0.00", CultureInfo.InvariantCulture)
                : string.Empty;
            string gpuVramUsedGiB = gpu?.VramUsedBytes is { } usedVram
                ? (usedVram / gib).ToString("0.00", CultureInfo.InvariantCulture)
                : string.Empty;

            sb.Append(snap.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(',')
              .Append(snap.ActiveHighestTier).Append(',')
              .Append(snap.Cpu.TotalLoadPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
              .Append(FormatNullable(snap.Cpu.CurrentClockMHz, "0")).Append(',')
              .Append(FormatNullable(snap.Cpu.PackageTemperatureCelsius, "0.0")).Append(',')
              .Append(ramUsedGiB).Append(',')
              .Append(ramTotalGiB).Append(',')
              .Append(snap.Memory.LoadPercent.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
              .Append(EscapeCsv(gpu?.Name ?? string.Empty)).Append(',')
              .Append(FormatNullable(gpu?.CoreLoadPercent, "0.0")).Append(',')
              .Append(gpuVramUsedGiB).Append(',')
              .Append(FormatNullable(gpu?.TemperatureCelsius, "0.0")).Append(',')
              .Append(FormatNullable(peakDiskTemp, "0.0"))
              .AppendLine();
        }

        return sb.ToString();
    }

    private string BuildHtmlReport(IReadOnlyList<TelemetrySnapshot> samples)
    {
        var latest = samples.Count > 0 ? samples[^1] : null;
        var sb = new StringBuilder(4096 + samples.Count * 200);
        const double gib = 1024.0 * 1024.0 * 1024.0;

        string generatedLocal = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        string osEncoded = WebUtility.HtmlEncode(RuntimeInformation.OSDescription);
        string runtimeEncoded = WebUtility.HtmlEncode(RuntimeInformation.FrameworkDescription);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"uk\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\" />");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        sb.AppendLine("  <title>ARGUS — Системний звіт телеметрії</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root { color-scheme: dark; }");
        sb.AppendLine("    body { margin:0; padding:28px; background:#060916; color:#EDF1FF; font-family:'Segoe UI',Inter,sans-serif; }");
        sb.AppendLine("    .wrap { max-width:1160px; margin:0 auto; }");
        sb.AppendLine("    .header { display:flex; justify-content:space-between; align-items:flex-end; border-bottom:1px solid #242E63; padding-bottom:16px; margin-bottom:20px; }");
        sb.AppendLine("    h1 { margin:0; font-size:24px; letter-spacing:1.2px; color:#EDF1FF; }");
        sb.AppendLine("    .sub { color:#95A0CC; font-size:13px; margin-top:4px; }");
        sb.AppendLine("    .badge { background:#0E2E2B; border:1px solid #2DD4A8; color:#2DD4A8; padding:5px 10px; border-radius:6px; font-size:12px; font-weight:600; }");
        sb.AppendLine("    .grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(230px,1fr)); gap:14px; margin-bottom:22px; }");
        sb.AppendLine("    .card { background:#111735; border:1px solid #242E63; border-radius:10px; padding:14px 16px; }");
        sb.AppendLine("    .card h2 { margin:0 0 8px 0; font-size:13px; text-transform:uppercase; letter-spacing:.8px; color:#95A0CC; }");
        sb.AppendLine("    .metric { font-family:'Cascadia Mono',Consolas,monospace; font-size:22px; color:#38D9F5; font-weight:600; }");
        sb.AppendLine("    .meta { color:#95A0CC; font-size:12.5px; margin-top:6px; line-height:1.45; }");
        sb.AppendLine("    table { width:100%; border-collapse:collapse; background:#111735; border:1px solid #242E63; border-radius:10px; overflow:hidden; font-size:12.5px; }");
        sb.AppendLine("    th, td { padding:9px 11px; border-bottom:1px solid #1C244D; text-align:left; font-family:'Cascadia Mono',Consolas,monospace; }");
        sb.AppendLine("    th { background:#0B1026; color:#95A0CC; font-family:'Segoe UI',Inter,sans-serif; font-weight:600; }");
        sb.AppendLine("    tr:hover td { background:#171F45; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"wrap\">");
        sb.AppendLine("    <div class=\"header\">");
        sb.AppendLine("      <div>");
        sb.AppendLine("        <h1>ARGUS — Звіт телеметрії системи</h1>");
        sb.AppendLine($"        <div class=\"sub\">Сформовано: {WebUtility.HtmlEncode(generatedLocal)} · {osEncoded} · {runtimeEncoded}</div>");
        sb.AppendLine("      </div>");
        string tierText = latest?.ActiveHighestTier == SensorTier.SignedPawnIo
            ? "Рівень 2: Win32 + підписаний PawnIO"
            : "Рівень 1: Win32 / PDH / NVML / IOCTL";
        sb.AppendLine($"      <div class=\"badge\">{WebUtility.HtmlEncode(tierText)}</div>");
        sb.AppendLine("    </div>");

        if (latest is not null)
        {
            var gpu = latest.Gpus.FirstOrDefault();
            string cpuName = WebUtility.HtmlEncode(latest.Cpu.Name);
            string cpuLoad = latest.Cpu.TotalLoadPercent.ToString("0.0", CultureInfo.CurrentCulture);
            string cpuClock = latest.Cpu.CurrentClockMHz.HasValue ? $"{latest.Cpu.CurrentClockMHz.Value:0} МГц" : "н/д";
            string ramUsed = (latest.Memory.UsedBytes / gib).ToString("0.0", CultureInfo.CurrentCulture);
            string ramTotal = (latest.Memory.TotalBytes / gib).ToString("0.0", CultureInfo.CurrentCulture);
            string ramPct = latest.Memory.LoadPercent.ToString("0.0", CultureInfo.CurrentCulture);
            string gpuName = WebUtility.HtmlEncode(gpu?.Name ?? "Не визначено");
            string gpuLoad = gpu?.CoreLoadPercent.HasValue == true ? $"{gpu.CoreLoadPercent.Value:0}%" : "—";
            string gpuTemp = gpu?.TemperatureCelsius.HasValue == true ? $"{gpu.TemperatureCelsius.Value:0} °C" : "н/д";
            string diskLines = latest.Storage.Count > 0
                ? string.Join("<br/>", latest.Storage.Select(d =>
                    $"{WebUtility.HtmlEncode($"Диск {d.DiskNumber} · {d.Model}")} ({(d.TemperatureCelsius.HasValue ? $"{d.TemperatureCelsius.Value:0} °C" : "н/д")})"))
                : "Накопичувачі не виявлено";

            sb.AppendLine("    <div class=\"grid\">");
            sb.AppendLine($"      <div class=\"card\"><h2>Процесор</h2><div class=\"metric\">{cpuLoad}%</div><div class=\"meta\">{cpuName}<br/>{WebUtility.HtmlEncode(cpuClock)} · {latest.Cpu.PhysicalCores}Я / {latest.Cpu.LogicalProcessors}П</div></div>");
            sb.AppendLine($"      <div class=\"card\"><h2>Оперативна пам'ять</h2><div class=\"metric\">{ramUsed} / {ramTotal} ГіБ</div><div class=\"meta\">Зайнято: {ramPct}% · Поріг: {_settingsStore.Current.RamUsageAlertPercent:0}%</div></div>");
            sb.AppendLine($"      <div class=\"card\"><h2>Відеокарта</h2><div class=\"metric\">{gpuLoad}</div><div class=\"meta\">{gpuName}<br/>Температура: {WebUtility.HtmlEncode(gpuTemp)}</div></div>");
            sb.AppendLine($"      <div class=\"card\"><h2>Накопичувачі</h2><div class=\"metric\">{latest.Storage.Count} шт.</div><div class=\"meta\">{diskLines}</div></div>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("    <table>");
        sb.AppendLine("      <thead><tr><th>Час (локальний)</th><th>CPU %</th><th>CPU МГц</th><th>ОЗП %</th><th>GPU %</th><th>GPU °C</th><th>Пік диска °C</th></tr></thead>");
        sb.AppendLine("      <tbody>");

        if (samples.Count == 0)
        {
            sb.AppendLine("        <tr><td colspan=\"7\">Історія телеметрії поки не містить зразків.</td></tr>");
        }
        else
        {
            foreach (var snap in samples)
            {
                var gpu = snap.Gpus.FirstOrDefault();
                double? peakDiskTemp = snap.Storage
                    .Where(static d => d.TemperatureCelsius.HasValue)
                    .Select(static d => d.TemperatureCelsius)
                    .Max();

                string timeLocal = snap.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                string cpuPct = snap.Cpu.TotalLoadPercent.ToString("0.0", CultureInfo.InvariantCulture);
                string cpuMhz = snap.Cpu.CurrentClockMHz?.ToString("0", CultureInfo.InvariantCulture) ?? "—";
                string ramPct = snap.Memory.LoadPercent.ToString("0.0", CultureInfo.InvariantCulture);
                string gpuPct = gpu?.CoreLoadPercent?.ToString("0.0", CultureInfo.InvariantCulture) ?? "—";
                string gpuTemp = gpu?.TemperatureCelsius?.ToString("0", CultureInfo.InvariantCulture) ?? "—";
                string diskTemp = peakDiskTemp?.ToString("0", CultureInfo.InvariantCulture) ?? "—";

                sb.AppendLine($"        <tr><td>{timeLocal}</td><td>{cpuPct}</td><td>{cpuMhz}</td><td>{ramPct}</td><td>{gpuPct}</td><td>{gpuTemp}</td><td>{diskTemp}</td></tr>");
            }
        }

        sb.AppendLine("      </tbody>");
        sb.AppendLine("    </table>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string FormatNullable(double? value, string format) =>
        value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : string.Empty;

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }
}
