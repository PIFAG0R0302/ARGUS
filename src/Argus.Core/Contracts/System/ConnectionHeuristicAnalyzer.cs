using System.Net;
using System.Net.Sockets;

namespace Argus.Core.Contracts.System;

public sealed record ConnectionHeuristicResult(
    ConnectionSuspicionLevel Level,
    ConnectionSuspicionFlags Flags,
    string? Summary);

/// <summary>
/// Lightweight, zero-overhead heuristic analyzer for active Windows network connections.
/// Detects masqueraded system processes, risky execution paths (Temp/Public/Root/AppData/ProgramData),
/// common mining/C2/IRC ports, hidden/pathless non-system processes, and external IPs lacking PTR on non-standard ports.
/// </summary>
public static class ConnectionHeuristicAnalyzer
{
    private static readonly HashSet<int> DangerousRemotePorts =
    [
        3333, 4444, 5555, 6666, 6667, 6697, 7777, 8888, 9999,
        14433, 14444, 31337, 45700
    ];

    private static readonly HashSet<int> StandardExternalPorts =
    [
        53, 80, 123, 443, 853, 8080, 8443
    ];

    private static readonly HashSet<string> System32ExpectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost",
        "csrss",
        "lsass",
        "smss",
        "wininit",
        "services",
        "winlogon",
        "dwm",
        "conhost",
        "dllhost",
        "taskhostw",
        "spoolsv",
        "lsaiso"
    };

    private static readonly HashSet<string> KnownElevatedOrKernelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "System Idle",
        "Idle",
        "Registry",
        "Secure System",
        "Memory Compression",
        "svchost",
        "lsass",
        "services",
        "csrss",
        "wininit",
        "winlogon",
        "MsMpEng",
        "NisSrv",
        "SecurityHealthService",
        "SearchIndexer",
        "spoolsv",
        "WmiPrvSE"
    };

    public static ConnectionHeuristicResult Evaluate(
        ActiveConnectionItem connection,
        bool isAdministrator,
        bool? hasReverseDnsPtr = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var flags = ConnectionSuspicionFlags.None;
        var level = ConnectionSuspicionLevel.None;
        var reasons = new List<string>(4);

        string normalizedName = NormalizeProcessName(connection.ProcessName);
        string? exePath = string.IsNullOrWhiteSpace(connection.ExecutablePath) ? null : connection.ExecutablePath.Trim();
        bool isExternalRemote = IsExternalPublicIp(connection.RemoteAddress);
        bool isEstablishedOrActive = string.Equals(connection.State, "ESTABLISHED", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(connection.State, "SYN_SENT", StringComparison.OrdinalIgnoreCase);

        // 1. Masquerading system process outside Windows\System32 (or Windows for explorer.exe) -> HIGH (Suspicious)
        if (exePath is not null)
        {
            if (System32ExpectedProcesses.Contains(normalizedName) && !IsInWindowsSystemDir(exePath))
            {
                flags |= ConnectionSuspicionFlags.SystemProcessMasquerading;
                level = MaxLevel(level, ConnectionSuspicionLevel.Suspicious);
                reasons.Add($"Невідповідний шлях системного процесу {normalizedName}: {exePath}");
            }
            else if (string.Equals(normalizedName, "explorer", StringComparison.OrdinalIgnoreCase) && !IsInWindowsRootDir(exePath))
            {
                flags |= ConnectionSuspicionFlags.SystemProcessMasquerading;
                level = MaxLevel(level, ConnectionSuspicionLevel.Suspicious);
                reasons.Add($"Невідповідний шлях explorer.exe: {exePath}");
            }

            // 2. High-risk execution paths: Temp, Users\Public, or drive root -> HIGH (Suspicious)
            if (IsTempOrPublicPath(exePath))
            {
                flags |= ConnectionSuspicionFlags.TempOrPublicPath;
                level = MaxLevel(level, ConnectionSuspicionLevel.Suspicious);
                reasons.Add("Запуск із Temp або Users\\Public");
            }
            else if (IsDriveRootPath(exePath))
            {
                flags |= ConnectionSuspicionFlags.RootDrivePath;
                level = MaxLevel(level, ConnectionSuspicionLevel.Suspicious);
                reasons.Add("Запуск із кореня диска");
            }
            // 3. AppData\Roaming or ProgramData on external active connections -> MEDIUM (Warning)
            else if ((isExternalRemote || isEstablishedOrActive) && !connection.IsLoopback && IsAppDataRoamingOrProgramData(exePath))
            {
                flags |= ConnectionSuspicionFlags.AppDataOrProgramDataPath;
                level = MaxLevel(level, ConnectionSuspicionLevel.Warning);
                reasons.Add("Запуск із AppData\\Roaming або ProgramData");
            }
        }
        else
        {
            // 4. Hidden / unknown process without valid name or path -> MEDIUM (Warning)
            bool isNameless = string.IsNullOrWhiteSpace(normalizedName) ||
                              normalizedName.StartsWith("PID ", StringComparison.OrdinalIgnoreCase);
            bool isExpectedKernelOrElevated = connection.Pid is 0 or 4 ||
                                              KnownElevatedOrKernelNames.Contains(normalizedName);

            if (isNameless)
            {
                flags |= ConnectionSuspicionFlags.HiddenOrUnknownProcess;
                level = MaxLevel(level, ConnectionSuspicionLevel.Suspicious);
                reasons.Add("Процес без імені або прихований PID");
            }
            else if (isExternalRemote && (!isExpectedKernelOrElevated || (isAdministrator && connection.Pid is not (0 or 4))))
            {
                flags |= ConnectionSuspicionFlags.HiddenOrUnknownProcess;
                level = MaxLevel(level, ConnectionSuspicionLevel.Warning);
                reasons.Add("Невідомий шлях виконуваного файлу");
            }
        }

        // 5. Known mining pool / C2 / IRC dangerous remote ports -> HIGH (Suspicious)
        if (connection.RemotePort > 0 &&
            DangerousRemotePorts.Contains(connection.RemotePort) &&
            !connection.IsLoopback)
        {
            flags |= ConnectionSuspicionFlags.DangerousPort;
            level = MaxLevel(level, ConnectionSuspicionLevel.Suspicious);
            reasons.Add($"Небезпечний порт (майнінг/C2/IRC): {connection.RemotePort}");
        }

        // 6. External public IP without Reverse DNS (PTR) on non-standard ports -> MEDIUM (Warning)
        if (hasReverseDnsPtr == false &&
            isExternalRemote &&
            connection.RemotePort > 0 &&
            !StandardExternalPorts.Contains(connection.RemotePort))
        {
            flags |= ConnectionSuspicionFlags.ExternalIpWithoutPtr;
            level = MaxLevel(level, ConnectionSuspicionLevel.Warning);
            reasons.Add($"Прямий зовнішній IP без PTR (порт {connection.RemotePort})");
        }

        string? summary = reasons.Count > 0 ? string.Join(" · ", reasons) : null;
        return new ConnectionHeuristicResult(level, flags, summary);
    }

    public static bool ShouldCheckReverseDns(ActiveConnectionItem connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return !connection.IsLoopback &&
               connection.RemotePort > 0 &&
               !StandardExternalPorts.Contains(connection.RemotePort) &&
               IsExternalPublicIp(connection.RemoteAddress);
    }

    public static bool IsExternalPublicIp(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address is "*" or "—" or "0.0.0.0" or "::")
        {
            return false;
        }

        if (!IPAddress.TryParse(address.Trim(), out IPAddress? ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return false;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127)
            {
                return false;
            }

            if (b[0] == 169 && b[1] == 254)
            {
                return false; // APIPA link-local
            }

            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            {
                return false; // RFC 1918
            }

            if (b[0] == 192 && b[1] == 168)
            {
                return false; // RFC 1918
            }

            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            {
                return false; // CGNAT
            }

            if (b[0] >= 224)
            {
                return false; // Multicast / reserved
            }

            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal)
            {
                return false;
            }

            byte[] b = ip.GetAddressBytes();
            // Unique local address fc00::/7
            if ((b[0] & 0xFE) == 0xFC)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private static ConnectionSuspicionLevel MaxLevel(ConnectionSuspicionLevel a, ConnectionSuspicionLevel b) =>
        (ConnectionSuspicionLevel)Math.Max((int)a, (int)b);

    private static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    private static bool IsInWindowsSystemDir(string exePath)
    {
        string normalized = exePath.Replace('/', '\\');
        return normalized.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInWindowsRootDir(string exePath)
    {
        string normalized = exePath.Replace('/', '\\');
        string? dir = Path.GetDirectoryName(normalized);
        return !string.IsNullOrWhiteSpace(dir) &&
               (dir.EndsWith(@":\Windows", StringComparison.OrdinalIgnoreCase) ||
                dir.EndsWith(@"\Windows\System32", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTempOrPublicPath(string exePath)
    {
        string normalized = exePath.Replace('/', '\\');
        return normalized.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\Windows\Temp\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\Users\Public\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAppDataRoamingOrProgramData(string exePath)
    {
        string normalized = exePath.Replace('/', '\\');
        return normalized.Contains(@"\AppData\Roaming\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@":\ProgramData\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDriveRootPath(string exePath)
    {
        string normalized = exePath.Replace('/', '\\');
        string? dir = Path.GetDirectoryName(normalized);
        return !string.IsNullOrWhiteSpace(dir) &&
               dir.Length == 3 &&
               char.IsLetter(dir[0]) &&
               dir[1] == ':' &&
               dir[2] == '\\';
    }
}
