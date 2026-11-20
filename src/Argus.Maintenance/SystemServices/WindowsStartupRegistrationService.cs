using Argus.Core.Contracts.System;
using Microsoft.Win32;

namespace Argus.Maintenance.SystemServices;

/// <summary>
/// Manages per-user Windows auto-start via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public sealed class WindowsStartupRegistrationService : IStartupRegistrationService
{
    public const string DefaultRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "Argus";

    private readonly string _subKeyPath;
    private readonly string _valueName;

    public WindowsStartupRegistrationService(
        string subKeyPath = DefaultRunSubKey,
        string valueName = DefaultValueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        _subKeyPath = subKeyPath;
        _valueName = valueName;
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath, writable: false);
        object? value = key?.GetValue(_valueName);
        return value is string s && !string.IsNullOrWhiteSpace(s);
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        if (enabled)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
            using var key = Registry.CurrentUser.CreateSubKey(_subKeyPath, writable: true);
            string command = $"\"{executablePath.Trim()}\" --minimized";
            key.SetValue(_valueName, command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath, writable: true);
            if (key?.GetValue(_valueName) is not null)
            {
                key.DeleteValue(_valueName, throwOnMissingValue: false);
            }
        }
    }
}
