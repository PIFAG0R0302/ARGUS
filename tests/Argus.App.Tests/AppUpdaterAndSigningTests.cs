using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Argus.App.ViewModels;
using Argus.Core.Configuration;
using Argus.Core.Contracts.Maintenance;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Argus.Core.Journal;
using Argus.Maintenance.SystemServices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Argus.App.Tests;

public sealed class AppUpdaterAndSigningTests : IDisposable
{
    private const string TrustedThumbprint = "9D1B22482AC32CEA02F398527F9409FCA09EA78D";
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ArgusUpdaterTests_{Guid.NewGuid():N}");

    public AppUpdaterAndSigningTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task GitHubAppUpdateService_DetectsNewerRelease_VerifiesSha256AndAuthenticodeThumbprint()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var settingsStore = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "settings.json"));
        await settingsStore.SaveAsync(new ArgusSettings
        {
            GitHubUpdateRepository = "argus-org/ARGUS",
            TrustedSignerThumbprint = TrustedThumbprint
        }, ct);

        using var journal = new FileBackedOperationJournal(
            eventBus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "journal.jsonl"));

        byte[] fakeInstallerBytes = Encoding.UTF8.GetBytes("FAKE-SIGNED-ARGUS-INSTALLER-V1.1.0-PAYLOAD");
        string expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(fakeInstallerBytes));
        string sumsBody = $"{expectedSha256}  Argus-Setup-1.1.0-win-x64.exe\n";

        var handler = new FakeGitHubHttpHandler(
            latestTag: "v1.1.0",
            releaseName: "ARGUS v1.1.0 Release",
            installerBytes: fakeInstallerBytes,
            sumsBody: sumsBody);

        AppUpdateCheckResult? publishedEvent = null;
        using var sub = eventBus.Subscribe<UpdateAvailableEvent>(e => publishedEvent = e.Update);

        using var updater = new GitHubAppUpdateService(
            settingsStore,
            journal,
            eventBus,
            httpHandler: handler,
            currentVersionOverride: "1.0.0",
            updatesDirectoryOverride: Path.Combine(_tempDir, "updates"),
            signatureInspector: static _ => (true, TrustedThumbprint, "Argus Open Source Release"));

        var check = await updater.CheckForUpdateAsync(ct);

        Assert.True(check.Succeeded, check.StatusMessage);
        Assert.True(check.IsUpdateAvailable);
        Assert.Equal("1.0.0", check.CurrentVersion);
        Assert.Equal("1.1.0", check.LatestVersion);
        Assert.Equal("Argus-Setup-1.1.0-win-x64.exe", check.InstallerFileName);
        Assert.NotNull(publishedEvent);

        var download = await updater.DownloadAndVerifyUpdateAsync(check, cancellationToken: ct);
        Assert.True(download.Succeeded, download.StatusMessage);
        Assert.True(download.SignatureVerified);
        Assert.Equal(expectedSha256, download.VerifiedSha256);
        Assert.Equal(TrustedThumbprint, download.SignerThumbprint);
        Assert.NotNull(download.LocalInstallerPath);
        Assert.True(File.Exists(download.LocalInstallerPath));
    }

    [Fact]
    public async Task GitHubAppUpdateService_BlocksTamperedSha256OrUntrustedCertificateAndDeletesFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var settingsStore = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "settings_tamper.json"));
        await settingsStore.SaveAsync(new ArgusSettings
        {
            GitHubUpdateRepository = "argus-org/ARGUS",
            TrustedSignerThumbprint = TrustedThumbprint
        }, ct);

        using var journal = new FileBackedOperationJournal(
            eventBus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "journal_tamper.jsonl"));

        byte[] fakeInstallerBytes = Encoding.UTF8.GetBytes("TAMPERED-INSTALLER-BYTES");
        string realSha256 = Convert.ToHexStringLower(SHA256.HashData(fakeInstallerBytes));
        string wrongSumsBody = $"{new string('a', 64)}  Argus-Setup-1.1.0-win-x64.exe\n";

        string updatesDir = Path.Combine(_tempDir, "updates_tamper");

        // 1. Checksum mismatch -> blocked & file deleted
        using (var badHashUpdater = new GitHubAppUpdateService(
            settingsStore,
            journal,
            eventBus,
            httpHandler: new FakeGitHubHttpHandler("v1.1.0", "ARGUS v1.1.0", fakeInstallerBytes, wrongSumsBody),
            currentVersionOverride: "1.0.0",
            updatesDirectoryOverride: updatesDir,
            signatureInspector: static _ => (true, TrustedThumbprint, "Argus")))
        {
            var check = await badHashUpdater.CheckForUpdateAsync(ct);
            var badDownload = await badHashUpdater.DownloadAndVerifyUpdateAsync(check, cancellationToken: ct);
            Assert.False(badDownload.Succeeded);
            Assert.Null(badDownload.LocalInstallerPath);
            Assert.Empty(Directory.GetFiles(updatesDir));
        }

        // 2. Valid SHA-256 but untrusted certificate thumbprint -> blocked & file deleted
        string goodSumsBody = $"{realSha256}  Argus-Setup-1.1.0-win-x64.exe\n";
        using (var badSignerUpdater = new GitHubAppUpdateService(
            settingsStore,
            journal,
            eventBus,
            httpHandler: new FakeGitHubHttpHandler("v1.1.0", "ARGUS v1.1.0", fakeInstallerBytes, goodSumsBody),
            currentVersionOverride: "1.0.0",
            updatesDirectoryOverride: updatesDir,
            signatureInspector: static _ => (true, "0000000000000000000000000000000000000000", "Untrusted Signer")))
        {
            var check = await badSignerUpdater.CheckForUpdateAsync(ct);
            var untrustedDownload = await badSignerUpdater.DownloadAndVerifyUpdateAsync(check, cancellationToken: ct);
            Assert.False(untrustedDownload.Succeeded);
            Assert.False(untrustedDownload.SignatureVerified);
            Assert.Null(untrustedDownload.LocalInstallerPath);
            Assert.Empty(Directory.GetFiles(updatesDir));
        }
    }

    [Fact]
    public async Task SettingsViewModel_CheckForUpdatesAndConfirmationWorkflow_UpdatesStateCleanly()
    {
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var settingsStore = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "settings_vm.json"));
        using var journal = new FileBackedOperationJournal(
            eventBus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "journal_vm.jsonl"));

        var fakeUpdateService = new StubAppUpdateService();
        bool exitRequested = false;

        using var vm = new SettingsViewModel(
            settingsStore,
            journal,
            eventBus,
            new StubStartupService(),
            new StubRestoreService(),
            uiDispatch: static a => a(),
            updateService: fakeUpdateService,
            requestAppExit: () => exitRequested = true);

        await vm.CheckForUpdatesAsync();
        Assert.True(vm.IsUpdateAvailable);
        Assert.Contains("v1.1.0", vm.AvailableUpdateTitle, StringComparison.Ordinal);

        vm.RequestInstallUpdateCommand.Execute(null);
        Assert.True(vm.IsUpdateConfirmationOpen);

        vm.CancelUpdateConfirmationCommand.Execute(null);
        Assert.False(vm.IsUpdateConfirmationOpen);

        vm.RequestInstallUpdateCommand.Execute(null);
        await vm.ConfirmAndInstallUpdateAsync();

        Assert.True(fakeUpdateService.Launched);
        Assert.True(exitRequested);
    }

    [Fact]
    public async Task GitHubAppUpdateService_PrefersZipPatchAndGeneratesInPlaceApplyCmdScript()
    {
        var ct = TestContext.Current.CancellationToken;
        var eventBus = new ArgusEventBus(NullLogger<ArgusEventBus>.Instance);
        using var settingsStore = new JsonArgusSettingsStore(
            NullLogger<JsonArgusSettingsStore>.Instance,
            Path.Combine(_tempDir, "settings_zip.json"));
        await settingsStore.SaveAsync(new ArgusSettings
        {
            GitHubUpdateRepository = "argus-org/ARGUS",
            TrustedSignerThumbprint = TrustedThumbprint
        }, ct);

        using var journal = new FileBackedOperationJournal(
            eventBus,
            NullLogger<FileBackedOperationJournal>.Instance,
            Path.Combine(_tempDir, "journal_zip.jsonl"));

        // Build a real valid ZIP patch archive containing Argus.exe
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("Argus.exe");
                await using var entryStream = entry.Open();
                byte[] exeData = Encoding.UTF8.GetBytes("SIGNED-ARGUS-V1.0.2-EXE");
                await entryStream.WriteAsync(exeData, ct);
            }

            zipBytes = ms.ToArray();
        }

        string zipSha256 = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
        string sumsBody = $"{zipSha256}  Argus-Portable-1.0.2-win-x64.zip\n";

        string updatesDir = Path.Combine(_tempDir, "updates_zip");
        using var updater = new GitHubAppUpdateService(
            settingsStore,
            journal,
            eventBus,
            httpHandler: new FakeGitHubZipHttpHandler("v1.0.2", zipBytes, sumsBody),
            currentVersionOverride: "1.0.1",
            updatesDirectoryOverride: updatesDir,
            signatureInspector: path => (path.EndsWith("Argus.exe", StringComparison.OrdinalIgnoreCase), TrustedThumbprint, "Argus Open Source Release"));

        var check = await updater.CheckForUpdateAsync(ct);
        Assert.True(check.Succeeded);
        Assert.True(check.IsUpdateAvailable);
        Assert.Equal("Argus-Portable-1.0.2-win-x64.zip", check.InstallerFileName);

        var download = await updater.DownloadAndVerifyUpdateAsync(check, cancellationToken: ct);
        Assert.True(download.Succeeded, download.StatusMessage);
        Assert.True(download.SignatureVerified);
        Assert.NotNull(download.LocalInstallerPath);
        Assert.EndsWith("apply-update.cmd", download.LocalInstallerPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(download.LocalInstallerPath));

        string cmdContent = await File.ReadAllTextAsync(download.LocalInstallerPath, ct);
        Assert.Contains("copy /Y \"%STAGE%\\Argus.exe\" \"%DEST%\\Argus.exe\"", cmdContent, StringComparison.Ordinal);
        Assert.Contains("set \"VER=1.0.2\"", cmdContent, StringComparison.Ordinal);
        Assert.Contains("DisplayVersion", cmdContent, StringComparison.Ordinal);
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
            // Ignore cleanup races
        }
    }

    private sealed class StubStartupService : IStartupRegistrationService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled, string executablePath) { }
    }

    private sealed class StubRestoreService : IRestorePointService
    {
        public Task<RestorePointResult> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default)
            => Task.FromResult(new RestorePointResult(true, description, "OK"));
    }

    private sealed class StubAppUpdateService : IAppUpdateService
    {
        public string CurrentVersion => "1.0.0";
        public string CurrentExecutableSignerSummary => "Підписано: Argus · 9D1B22482AC32CEA…";
        public bool Launched { get; private set; }

        public Task<AppUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppUpdateCheckResult(
                Succeeded: true,
                IsUpdateAvailable: true,
                CurrentVersion: "1.0.0",
                LatestVersion: "1.1.0",
                ReleaseTitle: "ARGUS v1.1.0",
                ReleaseNotes: "Додано автоматичне оновлення з GitHub і цифровий підпис.",
                InstallerFileName: "Argus-Setup-1.1.0-win-x64.exe",
                InstallerDownloadUrl: "https://github.com/argus-org/ARGUS/releases/download/v1.1.0/Argus-Setup-1.1.0-win-x64.exe",
                ChecksumsDownloadUrl: "https://github.com/argus-org/ARGUS/releases/download/v1.1.0/SHA256SUMS.txt",
                HtmlReleaseUrl: "https://github.com/argus-org/ARGUS/releases/tag/v1.1.0",
                StatusMessage: "Знайдено нову версію v1.1.0."));

        public Task<AppUpdateDownloadResult> DownloadAndVerifyUpdateAsync(
            AppUpdateCheckResult update,
            IProgress<(int Percent, string Stage)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report((100, "Готово"));
            return Task.FromResult(new AppUpdateDownloadResult(
                Succeeded: true,
                LocalInstallerPath: "C:\\Temp\\Argus-Setup-1.1.0-win-x64.exe",
                VerifiedSha256: new string('f', 64),
                SignerThumbprint: TrustedThumbprint,
                SignatureVerified: true,
                StatusMessage: "Інсталятор перевірено."));
        }

        public bool LaunchInstaller(string installerPath)
        {
            Launched = true;
            return true;
        }
    }

    private sealed class FakeGitHubHttpHandler(
        string latestTag,
        string releaseName,
        byte[] installerBytes,
        string sumsBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase))
            {
                string json = $$"""
                {
                  "tag_name": "{{latestTag}}",
                  "name": "{{releaseName}}",
                  "body": "Notes for {{latestTag}}",
                  "html_url": "https://github.com/argus-org/ARGUS/releases/tag/{{latestTag}}",
                  "assets": [
                    {
                      "name": "Argus-Setup-1.1.0-win-x64.exe",
                      "browser_download_url": "https://github.com/argus-org/ARGUS/releases/download/{{latestTag}}/Argus-Setup-1.1.0-win-x64.exe"
                    },
                    {
                      "name": "SHA256SUMS.txt",
                      "browser_download_url": "https://github.com/argus-org/ARGUS/releases/download/{{latestTag}}/SHA256SUMS.txt"
                    }
                  ]
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            if (url.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sumsBody, Encoding.UTF8, "text/plain")
                });
            }

            if (url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var content = new ByteArrayContent(installerBytes);
                content.Headers.ContentLength = installerBytes.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeGitHubZipHttpHandler(string latestTag, byte[] zipBytes, string sumsBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase))
            {
                string json = $$"""
                {
                  "tag_name": "{{latestTag}}",
                  "name": "ARGUS {{latestTag}}",
                  "body": "In-place patch update",
                  "html_url": "https://github.com/argus-org/ARGUS/releases/tag/{{latestTag}}",
                  "assets": [
                    {
                      "name": "Argus-Setup-1.0.2-win-x64.exe",
                      "browser_download_url": "https://github.com/argus-org/ARGUS/releases/download/{{latestTag}}/Argus-Setup-1.0.2-win-x64.exe"
                    },
                    {
                      "name": "Argus-Portable-1.0.2-win-x64.zip",
                      "browser_download_url": "https://github.com/argus-org/ARGUS/releases/download/{{latestTag}}/Argus-Portable-1.0.2-win-x64.zip"
                    },
                    {
                      "name": "SHA256SUMS.txt",
                      "browser_download_url": "https://github.com/argus-org/ARGUS/releases/download/{{latestTag}}/SHA256SUMS.txt"
                    }
                  ]
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            if (url.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sumsBody, Encoding.UTF8, "text/plain")
                });
            }

            if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var content = new ByteArrayContent(zipBytes);
                content.Headers.ContentLength = zipBytes.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
