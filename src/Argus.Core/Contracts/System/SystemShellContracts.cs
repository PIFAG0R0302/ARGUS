namespace Argus.Core.Contracts.System;

public interface IPrivilegeService
{
    bool IsAdministrator { get; }
    bool TryRelaunchElevated(string[]? arguments = null);
    bool CanWriteToDirectory(string directoryPath) => true;
}

public interface IStartupRegistrationService
{
    bool IsEnabled();
    void SetEnabled(bool enabled, string executablePath);
}

public sealed record AppUpdateCheckResult(
    bool Succeeded,
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseTitle,
    string ReleaseNotes,
    string? InstallerFileName,
    string? InstallerDownloadUrl,
    string? ChecksumsDownloadUrl,
    string? HtmlReleaseUrl,
    string StatusMessage);

public sealed record AppUpdateDownloadResult(
    bool Succeeded,
    string? LocalInstallerPath,
    string? VerifiedSha256,
    string? SignerThumbprint,
    bool SignatureVerified,
    string StatusMessage);

public sealed record UpdateAvailableEvent(AppUpdateCheckResult Update);

public interface IAppUpdateService
{
    string CurrentVersion { get; }
    string CurrentExecutableSignerSummary { get; }
    Task<AppUpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default);
    Task<AppUpdateDownloadResult> DownloadAndVerifyUpdateAsync(
        AppUpdateCheckResult update,
        IProgress<(int Percent, string Stage)>? progress = null,
        CancellationToken cancellationToken = default);
    bool LaunchInstaller(string installerPath);
}
