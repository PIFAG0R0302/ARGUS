using System.Diagnostics;
using System.Security.Principal;
using Argus.Core.Contracts.System;
using Microsoft.Extensions.Logging;

namespace Argus.Maintenance.SystemServices;

public sealed class WindowsPrivilegeService : IPrivilegeService
{
    private readonly ILogger<WindowsPrivilegeService> _logger;

    public WindowsPrivilegeService(ILogger<WindowsPrivilegeService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to query current Windows privilege level.");
                return false;
            }
        }
    }

    public bool TryRelaunchElevated(string[]? arguments = null)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            _logger.LogWarning("Cannot relaunch elevated: executable path is unavailable.");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };

            if (arguments is { Length: > 0 })
            {
                foreach (string arg in arguments)
                {
                    psi.ArgumentList.Add(arg);
                }
            }

            // Allow the newly elevated instance to start cleanly while the current process exits.
            psi.ArgumentList.Add("--allow-multi-instance");

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "UAC elevation was cancelled or failed.");
            return false;
        }
    }
}
