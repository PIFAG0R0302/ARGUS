using System.Runtime.InteropServices;

namespace Argus.Maintenance.Disks;

public sealed record DiskDeletePolicyDecision(
    bool IsAllowed,
    bool IsProtectedPath,
    bool IsReparsePoint,
    string? BlockReason);

/// <summary>
/// Abstraction for moving an approved file or directory into the Windows Recycle Bin.
/// Allows unit tests to verify deletion behavior without touching the real shell Recycle Bin.
/// </summary>
public interface IRecycleBinDeleter
{
    (bool Succeeded, string? ErrorMessage) MoveToRecycleBin(string fullPath);
}

/// <summary>
/// Mandatory safety guardrails for disk space drill-down deletion.
/// Hard-blocks Windows OS/system paths, ARGUS runtime/data directories, volume roots, and ReparsePoints.
/// </summary>
public static class DiskDeleteSafetyPolicy
{
    private static readonly string[] SystemDriveProtectedSubtrees =
    [
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        @"Users\Default",
        @"Users\Public"
    ];

    private static readonly string[] AnyVolumeProtectedSubtrees =
    [
        "System Volume Information",
        "$Recycle.Bin",
        "Boot",
        "Recovery"
    ];

    private static readonly HashSet<string> ProtectedRootSystemFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys",
        "hiberfil.sys",
        "swapfile.sys"
    };

    public static DiskDeletePolicyDecision EvaluatePath(
        string? rawPath,
        bool isReparsePoint = false,
        string? systemDriveRoot = null,
        string? appBaseDirectory = null,
        string? argusLocalAppData = null)
    {
        if (isReparsePoint)
        {
            return new DiskDeletePolicyDecision(
                IsAllowed: false,
                IsProtectedPath: false,
                IsReparsePoint: true,
                BlockReason: "Символічні посилання та точки з'єднання (ReparsePoint) заблоковані для безпеки цільових даних.");
        }

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return new DiskDeletePolicyDecision(
                IsAllowed: false,
                IsProtectedPath: true,
                IsReparsePoint: false,
                BlockReason: "Порожній або некоректний шлях.");
        }

        string trimmed = rawPath.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new DiskDeletePolicyDecision(
                IsAllowed: false,
                IsProtectedPath: true,
                IsReparsePoint: false,
                BlockReason: "Мережеві та системні UNC-шляхи заборонено видаляти.");
        }

        string normalized;
        try
        {
            if (!Path.IsPathFullyQualified(trimmed))
            {
                return new DiskDeletePolicyDecision(
                    IsAllowed: false,
                    IsProtectedPath: true,
                    IsReparsePoint: false,
                    BlockReason: "Дозволено видаляти лише абсолютні локальні шляхи.");
            }

            normalized = NormalizePath(trimmed);
        }
        catch
        {
            return new DiskDeletePolicyDecision(
                IsAllowed: false,
                IsProtectedPath: true,
                IsReparsePoint: false,
                BlockReason: "Некоректний формат шляху.");
        }

        string? volumeRoot = Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(volumeRoot) || volumeRoot.Length < 2 || volumeRoot[1] != ':')
        {
            return new DiskDeletePolicyDecision(
                IsAllowed: false,
                IsProtectedPath: true,
                IsReparsePoint: false,
                BlockReason: "Дозволено видаляти лише локальні елементи на томах з літерою диска.");
        }

        string normalizedVolumeRoot = EnsureTrailingSlash(volumeRoot);
        if (string.Equals(normalized, normalizedVolumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new DiskDeletePolicyDecision(
                IsAllowed: false,
                IsProtectedPath: true,
                IsReparsePoint: false,
                BlockReason: "Корінь тому захищено від видалення.");
        }

        // Check protected root files on any volume (pagefile.sys, hiberfil.sys, swapfile.sys)
        string? parentDir = Path.GetDirectoryName(normalized);
        if (!string.IsNullOrEmpty(parentDir) &&
            string.Equals(EnsureTrailingSlash(parentDir), normalizedVolumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            string fileName = Path.GetFileName(normalized);
            if (ProtectedRootSystemFiles.Contains(fileName))
            {
                return new DiskDeletePolicyDecision(
                    IsAllowed: false,
                    IsProtectedPath: true,
                    IsReparsePoint: false,
                    BlockReason: $"Системний файл {fileName} захищено від видалення.");
            }
        }

        // Check protected subtrees on any volume (System Volume Information, $Recycle.Bin, Boot, Recovery)
        foreach (string relativeSubtree in AnyVolumeProtectedSubtrees)
        {
            string candidate = NormalizePath(Path.Combine(normalizedVolumeRoot, relativeSubtree));
            if (IsSameOrSubpath(normalized, candidate))
            {
                return new DiskDeletePolicyDecision(
                    IsAllowed: false,
                    IsProtectedPath: true,
                    IsReparsePoint: false,
                    BlockReason: $"Системна тека «{relativeSubtree}» захищена від видалення.");
            }
        }

        // Check protected subtrees on the system drive (Windows, Program Files, Program Files (x86), ProgramData, Users\Default, Users\Public)
        string effectiveSysDrive = ResolveSystemDriveRoot(systemDriveRoot);
        foreach (string relativeSubtree in SystemDriveProtectedSubtrees)
        {
            string candidate = NormalizePath(Path.Combine(effectiveSysDrive, relativeSubtree));
            if (IsSameOrSubpath(normalized, candidate))
            {
                return new DiskDeletePolicyDecision(
                    IsAllowed: false,
                    IsProtectedPath: true,
                    IsReparsePoint: false,
                    BlockReason: $"Системна тека «{relativeSubtree}» захищена від видалення.");
            }
        }

        // Block Users root itself (e.g. C:\Users), while allowing user-owned subfolders like C:\Users\Alice\Downloads
        string usersRoot = NormalizePath(Path.Combine(effectiveSysDrive, "Users"));
        if (string.Equals(normalized, usersRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new DiskDeletePolicyDecision(
                IsAllowed: false,
                IsProtectedPath: true,
                IsReparsePoint: false,
                BlockReason: "Коренева тека профілів «Users» захищена від видалення.");
        }

        // Protect AppContext.BaseDirectory (both deleting inside it and deleting an ancestor containing it)
        string? effectiveAppBase = TryNormalizeOptionalPath(appBaseDirectory ?? AppContext.BaseDirectory);
        if (!string.IsNullOrEmpty(effectiveAppBase) &&
            (IsSameOrSubpath(normalized, effectiveAppBase) || IsSameOrSubpath(effectiveAppBase, normalized)))
        {
            return new DiskDeletePolicyDecision(
                IsAllowed: false,
                IsProtectedPath: true,
                IsReparsePoint: false,
                BlockReason: "Тека застосунку ARGUS захищена від видалення.");
        }

        // Protect %LOCALAPPDATA%\Argus (both deleting inside it and deleting an ancestor containing it)
        string defaultLocalArgus = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Argus");
        string? effectiveLocalArgus = TryNormalizeOptionalPath(argusLocalAppData ?? defaultLocalArgus);
        if (!string.IsNullOrEmpty(effectiveLocalArgus) &&
            (IsSameOrSubpath(normalized, effectiveLocalArgus) || IsSameOrSubpath(effectiveLocalArgus, normalized)))
        {
            return new DiskDeletePolicyDecision(
                IsAllowed: false,
                IsProtectedPath: true,
                IsReparsePoint: false,
                BlockReason: "Робоча тека даних ARGUS захищена від видалення.");
        }

        return new DiskDeletePolicyDecision(
            IsAllowed: true,
            IsProtectedPath: false,
            IsReparsePoint: false,
            BlockReason: null);
    }

    public static DiskDeletePolicyDecision EvaluateExistingPath(
        string? rawPath,
        string? systemDriveRoot = null,
        string? appBaseDirectory = null,
        string? argusLocalAppData = null)
    {
        var pathDecision = EvaluatePath(
            rawPath,
            isReparsePoint: false,
            systemDriveRoot: systemDriveRoot,
            appBaseDirectory: appBaseDirectory,
            argusLocalAppData: argusLocalAppData);

        if (!pathDecision.IsAllowed)
        {
            return pathDecision;
        }

        string normalized = NormalizePath(rawPath!);
        if (!File.Exists(normalized) && !Directory.Exists(normalized))
        {
            return new DiskDeletePolicyDecision(
                IsAllowed: false,
                IsProtectedPath: false,
                IsReparsePoint: false,
                BlockReason: "Елемент не знайдено на диску.");
        }

        try
        {
            var attrs = File.GetAttributes(normalized);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return new DiskDeletePolicyDecision(
                    IsAllowed: false,
                    IsProtectedPath: false,
                    IsReparsePoint: true,
                    BlockReason: "Символічні посилання та точки з'єднання (ReparsePoint) заблоковані для безпеки цільових даних.");
            }
        }
        catch (Exception ex)
        {
            return new DiskDeletePolicyDecision(
                IsAllowed: false,
                IsProtectedPath: true,
                IsReparsePoint: false,
                BlockReason: $"Не вдалося перевірити атрибути елемента: {ex.Message}");
        }

        return pathDecision;
    }

    internal static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(EnsureTrailingSlash(full), EnsureTrailingSlash(root), StringComparison.OrdinalIgnoreCase))
        {
            return EnsureTrailingSlash(root);
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? TryNormalizeOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.IsPathFullyQualified(path) ? NormalizePath(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveSystemDriveRoot(string? overrideRoot)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return EnsureTrailingSlash(overrideRoot.Trim());
        }

        string? sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
        return string.IsNullOrWhiteSpace(sysRoot) ? @"C:\" : EnsureTrailingSlash(sysRoot);
    }

    private static string EnsureTrailingSlash(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static bool IsSameOrSubpath(string candidate, string basePath)
    {
        if (string.Equals(candidate, basePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = EnsureTrailingSlash(basePath);
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Moves files and directories to the Windows Recycle Bin via Shell32 SHFileOperationW
/// using properly aligned x64 / x86 blittable struct layouts.
/// </summary>
public sealed partial class WindowsShellRecycleBinDeleter : IRecycleBinDeleter
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    public unsafe (bool Succeeded, string? ErrorMessage) MoveToRecycleBin(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return (false, "Порожній шлях.");
        }

        string doubleNullTerminated = fullPath + "\0\0";
        ushort flags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent;

        int result;
        bool aborted;

        fixed (char* pFrom = doubleNullTerminated)
        {
            if (IntPtr.Size == 8)
            {
                var op64 = new ShFileOpStruct64
                {
                    Hwnd = IntPtr.Zero,
                    WFunc = FoDelete,
                    PFrom = (IntPtr)pFrom,
                    PTo = IntPtr.Zero,
                    FFlags = flags,
                    FAnyOperationsAborted = 0,
                    HNameMappings = IntPtr.Zero,
                    LpszProgressTitle = IntPtr.Zero
                };
                result = SHFileOperation64(ref op64);
                aborted = op64.FAnyOperationsAborted != 0;
            }
            else
            {
                var op32 = new ShFileOpStruct32
                {
                    Hwnd = IntPtr.Zero,
                    WFunc = FoDelete,
                    PFrom = (IntPtr)pFrom,
                    PTo = IntPtr.Zero,
                    FFlags = flags,
                    FAnyOperationsAborted = 0,
                    HNameMappings = IntPtr.Zero,
                    LpszProgressTitle = IntPtr.Zero
                };
                result = SHFileOperation32(ref op32);
                aborted = op32.FAnyOperationsAborted != 0;
            }
        }

        if (result == 0 && !aborted)
        {
            return (true, null);
        }

        if (aborted)
        {
            return (false, "Операцію переміщення у Кошик було перервано.");
        }

        return (false, $"Не вдалося перемістити у Кошик (код Shell32: 0x{result:X}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct64
    {
        public IntPtr Hwnd;
        public uint WFunc;
        public IntPtr PFrom;
        public IntPtr PTo;
        public ushort FFlags;
        public int FAnyOperationsAborted;
        public IntPtr HNameMappings;
        public IntPtr LpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ShFileOpStruct32
    {
        public IntPtr Hwnd;
        public uint WFunc;
        public IntPtr PFrom;
        public IntPtr PTo;
        public ushort FFlags;
        public int FAnyOperationsAborted;
        public IntPtr HNameMappings;
        public IntPtr LpszProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation64(ref ShFileOpStruct64 fileOp);

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation32(ref ShFileOpStruct32 fileOp);
}
