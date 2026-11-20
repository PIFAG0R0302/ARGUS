using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Argus.Core.Journal;

namespace Argus.Maintenance.SystemServices;

/// <summary>
/// GitHub Releases update checker and downloader with SHA-256 checksum verification
/// and native Windows CryptoAPI (crypt32.dll) Authenticode certificate thumbprint validation.
/// </summary>
public sealed partial class GitHubAppUpdateService : IAppUpdateService, IDisposable
{
    private const int CertQueryObjectFile = 1;
    private const int CertQueryContentFlagPkcs7SignedEmbed = 1 << 10;
    private const int CertQueryFormatFlagBinary = 1 << 1;
    private const uint CertSha1HashPropId = 3;
    private const uint CertSimpleDisplayType = 4;

    private readonly IArgusSettingsStore _settingsStore;
    private readonly IOperationJournal _journal;
    private readonly IArgusEventBus _eventBus;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _updatesDirectory;
    private readonly Func<string, (bool IsSigned, string? Thumbprint, string? Subject)> _signatureInspector;
    private bool _disposed;

    public GitHubAppUpdateService(
        IArgusSettingsStore settingsStore,
        IOperationJournal journal,
        IArgusEventBus eventBus,
        HttpMessageHandler? httpHandler = null,
        string? currentVersionOverride = null,
        string? updatesDirectoryOverride = null,
        Func<string, (bool IsSigned, string? Thumbprint, string? Subject)>? signatureInspector = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

        CurrentVersion = !string.IsNullOrWhiteSpace(currentVersionOverride)
            ? currentVersionOverride.Trim()
            : ResolveCurrentAssemblyVersion();

        _updatesDirectory = !string.IsNullOrWhiteSpace(updatesDirectoryOverride)
            ? updatesDirectoryOverride
            : ArgusPaths.GetDefaultUpdatesDirectory();

        _signatureInspector = signatureInspector ?? InspectAuthenticodeSignature;

        if (httpHandler is not null)
        {
            _httpClient = new HttpClient(httpHandler, disposeHandler: false);
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _ownsHttpClient = true;
        }

        _httpClient.DefaultRequestHeaders.UserAgent.Clear();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Argus-Desktop", CurrentVersion));
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        CurrentExecutableSignerSummary = BuildCurrentExecutableSignerSummary();
    }

    public string CurrentVersion { get; }

    public string CurrentExecutableSignerSummary { get; }

    public async Task<AppUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string repo = (_settingsStore.Current.GitHubUpdateRepository ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/', StringComparison.Ordinal))
        {
            return new AppUpdateCheckResult(
                Succeeded: false,
                IsUpdateAvailable: false,
                CurrentVersion: CurrentVersion,
                LatestVersion: CurrentVersion,
                ReleaseTitle: string.Empty,
                ReleaseNotes: string.Empty,
                InstallerFileName: null,
                InstallerDownloadUrl: null,
                ChecksumsDownloadUrl: null,
                HtmlReleaseUrl: null,
                StatusMessage: "Репозиторій GitHub для перевірки оновлень ще не вказано у форматі власник/репозиторій.");
        }

        string apiUrl = $"https://api.github.com/repos/{repo}/releases/latest";
        try
        {
            using var response = await _httpClient.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new AppUpdateCheckResult(
                    Succeeded: true,
                    IsUpdateAvailable: false,
                    CurrentVersion: CurrentVersion,
                    LatestVersion: CurrentVersion,
                    ReleaseTitle: string.Empty,
                    ReleaseNotes: string.Empty,
                    InstallerFileName: null,
                    InstallerDownloadUrl: null,
                    ChecksumsDownloadUrl: null,
                    HtmlReleaseUrl: $"https://github.com/{repo}/releases",
                    StatusMessage: $"У репозиторії {repo} ще немає опублікованих релізів. Установлено актуальну версію v{CurrentVersion}.");
            }

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp) ? (tagProp.GetString() ?? string.Empty) : string.Empty;
            string releaseTitle = root.TryGetProperty("name", out var nameProp) ? (nameProp.GetString() ?? tagName) : tagName;
            string releaseNotes = root.TryGetProperty("body", out var bodyProp) ? (bodyProp.GetString() ?? string.Empty) : string.Empty;
            string? htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : $"https://github.com/{repo}/releases";

            string latestVersionStr = NormalizeVersionString(tagName);
            bool isNewer = TryParseVersion(latestVersionStr, out var latestVer) &&
                           TryParseVersion(CurrentVersion, out var currentVer) &&
                           latestVer > currentVer;

            string? zipName = null;
            string? zipUrl = null;
            string? exeName = null;
            string? exeUrl = null;
            string? checksumsUrl = null;

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    string aName = asset.TryGetProperty("name", out var an) ? (an.GetString() ?? string.Empty) : string.Empty;
                    string? aUrl = asset.TryGetProperty("browser_download_url", out var au) ? au.GetString() : null;
                    if (string.IsNullOrWhiteSpace(aName) || string.IsNullOrWhiteSpace(aUrl))
                    {
                        continue;
                    }

                    if (aName.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        checksumsUrl = aUrl;
                    }
                    else if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                             (zipUrl is null || aName.Contains("Portable", StringComparison.OrdinalIgnoreCase)))
                    {
                        zipName = aName;
                        zipUrl = aUrl;
                    }
                    else if (aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                             (exeUrl is null || aName.Contains("Setup", StringComparison.OrdinalIgnoreCase)))
                    {
                        exeName = aName;
                        exeUrl = aUrl;
                    }
                }
            }

            // Prefer in-place ZIP patch archive so updates replace files directly without running a second setup wizard.
            string? installerName = zipUrl is not null ? zipName : exeName;
            string? installerUrl = zipUrl ?? exeUrl;

            string statusMsg = isNewer
                ? $"Знайдено нову версію v{latestVersionStr} на GitHub ({repo})."
                : $"Установлено найновішу версію ARGUS (v{CurrentVersion}).";

            var result = new AppUpdateCheckResult(
                Succeeded: true,
                IsUpdateAvailable: isNewer,
                CurrentVersion: CurrentVersion,
                LatestVersion: latestVersionStr,
                ReleaseTitle: releaseTitle,
                ReleaseNotes: releaseNotes,
                InstallerFileName: installerName,
                InstallerDownloadUrl: installerUrl,
                ChecksumsDownloadUrl: checksumsUrl,
                HtmlReleaseUrl: htmlUrl,
                StatusMessage: statusMsg);

            await _journal.RecordAsync(
                "argus.updater",
                OperationSeverity.Info,
                "UpdateCheckCompleted",
                $"{statusMsg} (Поточна=v{CurrentVersion}, Остання=v{latestVersionStr})").ConfigureAwait(false);

            if (isNewer)
            {
                _eventBus.Publish(new UpdateAvailableEvent(result));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AppUpdateCheckResult(
                Succeeded: false,
                IsUpdateAvailable: false,
                CurrentVersion: CurrentVersion,
                LatestVersion: CurrentVersion,
                ReleaseTitle: string.Empty,
                ReleaseNotes: string.Empty,
                InstallerFileName: null,
                InstallerDownloadUrl: null,
                ChecksumsDownloadUrl: null,
                HtmlReleaseUrl: $"https://github.com/{repo}/releases",
                StatusMessage: $"Не вдалося перевірити оновлення з GitHub ({repo}): {ex.Message}");
        }
    }

    public async Task<AppUpdateDownloadResult> DownloadAndVerifyUpdateAsync(
        AppUpdateCheckResult update,
        IProgress<(int Percent, string Stage)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(update);

        if (string.IsNullOrWhiteSpace(update.InstallerDownloadUrl))
        {
            return new AppUpdateDownloadResult(
                Succeeded: false,
                LocalInstallerPath: null,
                VerifiedSha256: null,
                SignerThumbprint: null,
                SignatureVerified: false,
                StatusMessage: "У релізі на GitHub не знайдено файлу інсталятора (.exe).");
        }

        Directory.CreateDirectory(_updatesDirectory);
        string fileName = !string.IsNullOrWhiteSpace(update.InstallerFileName)
            ? Path.GetFileName(update.InstallerFileName)
            : $"Argus-Portable-{update.LatestVersion}-win-x64.zip";
        string targetPath = Path.Combine(_updatesDirectory, fileName);
        string? stageDir = null;

        try
        {
            progress?.Report((5, $"Завантаження пакета оновлення {fileName} з GitHub…"));

            using (var response = await _httpClient.GetAsync(update.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? totalBytes = response.Content.Headers.ContentLength;

                await using var netStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

                byte[] buffer = new byte[65536];
                long readTotal = 0;
                int lastReportedPct = 5;
                int read;
                while ((read = await netStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    readTotal += read;

                    if (totalBytes is > 0)
                    {
                        int pct = Math.Clamp(5 + (int)(readTotal * 75L / totalBytes.Value), 5, 80);
                        if (pct >= lastReportedPct + 2)
                        {
                            lastReportedPct = pct;
                            double mbDone = readTotal / (1024.0 * 1024.0);
                            double mbTotal = totalBytes.Value / (1024.0 * 1024.0);
                            progress?.Report((pct, string.Format(CultureInfo.CurrentCulture, "Завантажено {0:0.0} із {1:0.0} МБ…", mbDone, mbTotal)));
                        }
                    }
                    else
                    {
                        double mbDone = readTotal / (1024.0 * 1024.0);
                        int pct = Math.Clamp(5 + (int)Math.Min(73, mbDone * 1.5), 5, 78);
                        if (pct >= lastReportedPct + 2)
                        {
                            lastReportedPct = pct;
                            progress?.Report((pct, string.Format(CultureInfo.CurrentCulture, "Завантажено {0:0.0} МБ…", mbDone)));
                        }
                    }
                }
            }

            progress?.Report((84, "Перевірка контрольної суми SHA-256…"));
            string actualSha256;
            await using (var verifyStream = File.OpenRead(targetPath))
            {
                byte[] hashBytes = await SHA256.HashDataAsync(verifyStream, cancellationToken).ConfigureAwait(false);
                actualSha256 = Convert.ToHexStringLower(hashBytes);
            }

            if (!string.IsNullOrWhiteSpace(update.ChecksumsDownloadUrl))
            {
                string sumsText = await _httpClient.GetStringAsync(update.ChecksumsDownloadUrl, cancellationToken).ConfigureAwait(false);
                string? expectedSha256 = TryExtractExpectedSha256(sumsText, fileName);
                if (!string.IsNullOrWhiteSpace(expectedSha256) &&
                    !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    SafeDeleteFile(targetPath);
                    string err = $"Помилка перевірки цілісності SHA-256 для {fileName}: очікувався {expectedSha256[..12]}…, отримано {actualSha256[..12]}…";
                    await _journal.RecordAsync("argus.updater", OperationSeverity.Error, "UpdateChecksumMismatch", err, succeeded: false).ConfigureAwait(false);
                    return new AppUpdateDownloadResult(false, null, actualSha256, null, false, err);
                }
            }

            bool isZipPatch = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            string fileToVerifySignature = targetPath;

            if (isZipPatch)
            {
                progress?.Report((88, "Розпакування пакета оновлення…"));
                stageDir = Path.Combine(_updatesDirectory, $"stage-{NormalizeVersionString(update.LatestVersion)}");
                SafeDeleteDirectory(stageDir);
                Directory.CreateDirectory(stageDir);
                ZipFile.ExtractToDirectory(targetPath, stageDir, overwriteFiles: true);

                string stagedExe = Path.Combine(stageDir, "Argus.exe");
                if (!File.Exists(stagedExe))
                {
                    SafeDeleteDirectory(stageDir);
                    SafeDeleteFile(targetPath);
                    const string err = "У пакеті оновлення (.zip) не знайдено файлу Argus.exe. Оновлення скасовано.";
                    await _journal.RecordAsync("argus.updater", OperationSeverity.Error, "UpdatePackageInvalid", err, succeeded: false).ConfigureAwait(false);
                    return new AppUpdateDownloadResult(false, null, actualSha256, null, false, err);
                }

                fileToVerifySignature = stagedExe;
            }

            progress?.Report((92, "Перевірка цифрового підпису Authenticode…"));
            var (isSigned, thumbprint, _) = _signatureInspector(fileToVerifySignature);
            string expectedThumbprint = (_settingsStore.Current.TrustedSignerThumbprint ?? string.Empty)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToUpperInvariant();

            if (!isSigned || string.IsNullOrWhiteSpace(thumbprint))
            {
                SafeDeleteDirectory(stageDir);
                SafeDeleteFile(targetPath);
                const string err = "Завантажене оновлення не має цифрового підпису Authenticode. Оновлення заблоковано.";
                await _journal.RecordAsync("argus.updater", OperationSeverity.Error, "UpdateUnsignedBlocked", err, succeeded: false).ConfigureAwait(false);
                return new AppUpdateDownloadResult(false, null, actualSha256, null, false, err);
            }

            if (!string.IsNullOrWhiteSpace(expectedThumbprint) &&
                !string.Equals(thumbprint, expectedThumbprint, StringComparison.OrdinalIgnoreCase))
            {
                SafeDeleteDirectory(stageDir);
                SafeDeleteFile(targetPath);
                string err = $"Відбиток сертифіката оновлення ({thumbprint}) не збігається з довіреним сертифікатом ARGUS ({expectedThumbprint}). Оновлення заблоковано.";
                await _journal.RecordAsync("argus.updater", OperationSeverity.Error, "UpdateUntrustedSignerBlocked", err, succeeded: false).ConfigureAwait(false);
                return new AppUpdateDownloadResult(false, null, actualSha256, thumbprint, false, err);
            }

            string launchPath = targetPath;
            if (isZipPatch && stageDir is not null)
            {
                progress?.Report((96, "Підготовка сценарію оновлення встановленої програми…"));
                launchPath = WriteInPlaceUpdateScript(stageDir, targetPath, NormalizeVersionString(update.LatestVersion));
            }

            progress?.Report((100, $"Оновлення v{update.LatestVersion} перевірено (SHA-256 + Authenticode). Застосування…"));

            await _journal.RecordAsync(
                "argus.updater",
                OperationSeverity.Success,
                "UpdateVerifiedReady",
                $"Файл={fileName}, SHA256={actualSha256[..16]}…, Сертифікат={thumbprint}").ConfigureAwait(false);

            return new AppUpdateDownloadResult(
                Succeeded: true,
                LocalInstallerPath: launchPath,
                VerifiedSha256: actualSha256,
                SignerThumbprint: thumbprint,
                SignatureVerified: true,
                StatusMessage: $"Оновлення v{update.LatestVersion} перевірено за SHA-256 і підписом ({thumbprint[..12]}…). Перезапуск…");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeDeleteDirectory(stageDir);
            SafeDeleteFile(targetPath);
            return new AppUpdateDownloadResult(
                Succeeded: false,
                LocalInstallerPath: null,
                VerifiedSha256: null,
                SignerThumbprint: null,
                SignatureVerified: false,
                StatusMessage: $"Помилка завантаження оновлення: {ex.Message}");
        }
    }

    public bool LaunchInstaller(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return false;
        }

        try
        {
            if (installerPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                string installDir = ResolveInstallDirectory();
                bool needsElevation = !CanWriteToDirectory(installDir);

                var cmdPsi = needsElevation
                    ? new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"\"{installerPath}\"\"",
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    }
                    : new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"\"{installerPath}\"\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                Process.Start(cmdPsi);
                return true;
            }

            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal string WriteInPlaceUpdateScript(string stageDir, string zipArchivePath, string newVersion)
    {
        string installDir = ResolveInstallDirectory();
        string targetExePath = Path.Combine(installDir, "Argus.exe");
        int currentPid = Environment.ProcessId;
        string scriptPath = Path.Combine(_updatesDirectory, "apply-update.cmd");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("setlocal enableextensions");
        sb.AppendLine($"set \"PID={currentPid}\"");
        sb.AppendLine($"set \"STAGE={stageDir}\"");
        sb.AppendLine($"set \"DEST={installDir}\"");
        sb.AppendLine($"set \"ZIP={zipArchivePath}\"");
        sb.AppendLine($"set \"VER={newVersion}\"");
        sb.AppendLine("set \"RETRIES=0\"");
        sb.AppendLine(":wait_exit");
        sb.AppendLine("tasklist /FI \"PID eq %PID%\" 2>nul | find /I \"%PID%\" >nul");
        sb.AppendLine("if %ERRORLEVEL%==0 (");
        sb.AppendLine("  set /a RETRIES+=1");
        sb.AppendLine("  if %RETRIES% LSS 20 (");
        sb.AppendLine("    timeout /t 1 /nobreak >nul");
        sb.AppendLine("    goto wait_exit");
        sb.AppendLine("  )");
        sb.AppendLine(")");
        sb.AppendLine("set \"COPY_TRY=0\"");
        sb.AppendLine(":copy_exe");
        sb.AppendLine("copy /Y \"%STAGE%\\Argus.exe\" \"%DEST%\\Argus.exe\" >nul 2>&1");
        sb.AppendLine("if errorlevel 1 (");
        sb.AppendLine("  set /a COPY_TRY+=1");
        sb.AppendLine("  if %COPY_TRY% LSS 8 (");
        sb.AppendLine("    timeout /t 1 /nobreak >nul");
        sb.AppendLine("    goto copy_exe");
        sb.AppendLine("  )");
        sb.AppendLine(")");
        sb.AppendLine("if exist \"%STAGE%\\USER-GUIDE.md\" copy /Y \"%STAGE%\\USER-GUIDE.md\" \"%DEST%\\USER-GUIDE.md\" >nul 2>&1");
        sb.AppendLine("if exist \"%STAGE%\\rules\" xcopy /E /I /Y \"%STAGE%\\rules\\*\" \"%DEST%\\rules\\\" >nul 2>&1");
        sb.AppendLine("set \"UNINST_KEY=HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{D4B9F2E1-8A4C-4F92-B73E-1C5E9F80A311}_is1\"");
        sb.AppendLine("reg query \"%UNINST_KEY%\" >nul 2>&1 && (");
        sb.AppendLine("  reg add \"%UNINST_KEY%\" /v DisplayVersion /t REG_SZ /d \"%VER%\" /f >nul 2>&1");
        sb.AppendLine("  reg add \"%UNINST_KEY%\" /v DisplayName /t REG_SZ /d \"ARGUS %VER%\" /f >nul 2>&1");
        sb.AppendLine(")");
        sb.AppendLine("rmdir /S /Q \"%STAGE%\" >nul 2>&1");
        sb.AppendLine("del /F /Q \"%ZIP%\" >nul 2>&1");
        sb.AppendLine($"start \"\" \"{targetExePath}\"");
        sb.AppendLine("(goto) 2>nul & del /F /Q \"%~f0\"");

        File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return scriptPath;
    }

    private static string ResolveInstallDirectory()
    {
        string? procPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(procPath))
        {
            string? dir = Path.GetDirectoryName(procPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool CanWriteToDirectory(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".argus_upd_probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SafeDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    internal static string? TryExtractExpectedSha256(string sumsContent, string fileName)
    {
        if (string.IsNullOrWhiteSpace(sumsContent) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (string rawLine in sumsContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length < 66 || !line.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string candidate = line[..64];
            if (candidate.All(static c => char.IsAsciiHexDigit(c)))
            {
                return candidate.ToLowerInvariant();
            }
        }

        return null;
    }

    internal static unsafe (bool IsSigned, string? Thumbprint, string? Subject) InspectAuthenticodeSignature(string filePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return (false, null, null);
        }

        IntPtr certStore = IntPtr.Zero;
        IntPtr cryptMsg = IntPtr.Zero;
        IntPtr certContext = IntPtr.Zero;
        try
        {
            if (!CryptQueryObject(
                CertQueryObjectFile,
                filePath,
                CertQueryContentFlagPkcs7SignedEmbed,
                CertQueryFormatFlagBinary,
                0,
                out _,
                out _,
                out _,
                out certStore,
                out cryptMsg,
                IntPtr.Zero) || certStore == IntPtr.Zero)
            {
                return (false, null, null);
            }

            byte* hashBuf = stackalloc byte[64];
            char* nameBuf = stackalloc char[256];
            string? fallbackThumbprint = null;
            string? fallbackSubject = null;

            while ((certContext = CertEnumCertificatesInStore(certStore, certContext)) != IntPtr.Zero)
            {
                uint hashLen = 64;
                string? thumbprint = null;
                if (CertGetCertificateContextProperty(certContext, CertSha1HashPropId, (IntPtr)hashBuf, ref hashLen) && hashLen > 0)
                {
                    thumbprint = Convert.ToHexString(new ReadOnlySpan<byte>(hashBuf, (int)hashLen));
                }

                uint chars = CertGetNameStringW(certContext, CertSimpleDisplayType, 0, IntPtr.Zero, (IntPtr)nameBuf, 256);
                string? subject = chars > 1 ? new string(nameBuf, 0, (int)chars - 1) : null;

                if (string.IsNullOrWhiteSpace(thumbprint))
                {
                    continue;
                }

                if (fallbackThumbprint is null)
                {
                    fallbackThumbprint = thumbprint;
                    fallbackSubject = subject;
                }

                if (!string.IsNullOrWhiteSpace(subject) &&
                    subject.Contains("Argus", StringComparison.OrdinalIgnoreCase))
                {
                    return (true, thumbprint, subject);
                }
            }

            return (!string.IsNullOrWhiteSpace(fallbackThumbprint), fallbackThumbprint, fallbackSubject);
        }
        catch
        {
            return (false, null, null);
        }
        finally
        {
            if (certContext != IntPtr.Zero)
            {
                _ = CertFreeCertificateContext(certContext);
            }

            if (cryptMsg != IntPtr.Zero)
            {
                _ = CryptMsgClose(cryptMsg);
            }

            if (certStore != IntPtr.Zero)
            {
                _ = CertCloseStore(certStore, 0);
            }
        }
    }

    private string BuildCurrentExecutableSignerSummary()
    {
        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
        {
            var (signed, thumb, subject) = _signatureInspector(exePath);
            if (signed && !string.IsNullOrWhiteSpace(thumb))
            {
                string subjPart = !string.IsNullOrWhiteSpace(subject) ? $"{subject} · " : string.Empty;
                return $"Підписано: {subjPart}{thumb[..Math.Min(16, thumb.Length)]}…";
            }
        }

        string configured = _settingsStore.Current.TrustedSignerThumbprint;
        return !string.IsNullOrWhiteSpace(configured)
            ? $"Довірений відбиток: {configured[..Math.Min(16, configured.Length)]}…"
            : "Цифровий підпис Authenticode";
    }

    private static string ResolveCurrentAssemblyVersion()
    {
        var ver = typeof(GitHubAppUpdateService).Assembly.GetName().Version;
        return ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
    }

    private static string NormalizeVersionString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "1.0.0";
        }

        string trimmed = raw.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..].Trim();
        }

        int dashIdx = trimmed.IndexOf('-');
        if (dashIdx > 0)
        {
            trimmed = trimmed[..dashIdx];
        }

        return trimmed;
    }

    private static bool TryParseVersion(string input, out Version version)
    {
        string clean = NormalizeVersionString(input);
        if (Version.TryParse(clean, out var parsed) && parsed is not null)
        {
            version = new Version(
                Math.Max(0, parsed.Major),
                Math.Max(0, parsed.Minor),
                Math.Max(0, parsed.Build));
            return true;
        }

        version = new Version(1, 0, 0);
        return false;
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptQueryObject(
        int dwObjectType,
        string pvObject,
        int dwExpectedContentTypeFlags,
        int dwExpectedFormatTypeFlags,
        int dwFlags,
        out int pdwMsgAndCertEncodingType,
        out int pdwContentType,
        out int pdwFormatType,
        out IntPtr phCertStore,
        out IntPtr phMsg,
        IntPtr ppvContext);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    private static partial IntPtr CertEnumCertificatesInStore(IntPtr hCertStore, IntPtr pPrevCertContext);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CertGetCertificateContextProperty(
        IntPtr pCertContext,
        uint dwPropId,
        IntPtr pvData,
        ref uint pcbData);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    private static partial uint CertGetNameStringW(
        IntPtr pCertContext,
        uint dwType,
        uint dwFlags,
        IntPtr pvTypePara,
        IntPtr pszNameString,
        uint cchNameString);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CertFreeCertificateContext(IntPtr pCertContext);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CertCloseStore(IntPtr hCertStore, uint dwFlags);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptMsgClose(IntPtr hCryptMsg);
}
