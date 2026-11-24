using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Argus.Core.Contracts.Devices;
using Microsoft.Win32;

namespace Argus.Devices.Providers;

/// <summary>
/// Full hardware inventory (WMI/SMBIOS/Registry/NetworkInterface) and high-speed live device tree
/// via native Win32 SetupAPI and CfgMgr32 with zero third-party drivers.
/// </summary>
public sealed partial class WindowsDeviceProvider : IDeviceProvider
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;

    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpService = 0x00000004;
    private const uint SpdrpClass = 0x00000007;
    private const uint SpdrpDriver = 0x00000009;
    private const uint SpdrpMfg = 0x0000000B;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint SpdrpEnumeratorName = 0x00000016;

    private const uint DnHasProblem = 0x00000400;
    private const uint CmProbDisabled = 22;
    private const int CrSuccess = 0;
    private const uint DiidFlagShowSearchUi = 0x00000001;
    private const int ErrorCancelled = 1223;

    internal enum NativeInstallStatus
    {
        Installed,
        Cancelled,
        Failed
    }

    internal readonly record struct NativeDriverInstallAttempt(
        NativeInstallStatus Status,
        bool RebootRequired,
        int LastWin32Error);

    public Task<HardwareInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CollectInventory(), cancellationToken);
    }

    public Task<IReadOnlyList<DeviceNode>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(EnumerateSetupApiDevices, cancellationToken);
    }

    public Task<DriverSearchResult> SearchAndUpdateDriverAsync(
        string deviceInstanceId,
        string? friendlyName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceInstanceId))
        {
            return Task.FromResult(new DriverSearchResult(
                Succeeded: false,
                Outcome: DriverSearchOutcome.Failed,
                Message: "Не вказано Instance ID пристрою для пошуку драйвера."));
        }

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new DriverSearchResult(
                Succeeded: false,
                Outcome: DriverSearchOutcome.Unsupported,
                Message: "Пошук драйверів підтримується лише в ОС Windows."));
        }

        string trimmedId = deviceInstanceId.Trim();
        string deviceLabel = !string.IsNullOrWhiteSpace(friendlyName) ? friendlyName.Trim() : trimmedId;

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ExecuteDriverSearch(
                trimmedId,
                deviceLabel,
                IsProcessElevated,
                TryInvokeNativeDiInstallDevice,
                TryLaunchElevatedDeviceProperties,
                TryRunPnpUtilScanDevices,
                TryOpenWindowsUpdateOptionalUpdates);
        }, cancellationToken);
    }

    internal static DriverSearchResult ExecuteDriverSearch(
        string deviceInstanceId,
        string deviceLabel,
        Func<bool> isElevated,
        Func<string, NativeDriverInstallAttempt> invokeDiInstallDevice,
        Func<string, bool> launchElevatedDeviceProperties,
        Action runPnpUtilScanDevices,
        Func<bool> openWindowsUpdateOptionalUpdates)
    {
        if (string.IsNullOrWhiteSpace(deviceInstanceId))
        {
            return new DriverSearchResult(
                Succeeded: false,
                Outcome: DriverSearchOutcome.Failed,
                Message: "Не вказано Instance ID пристрою для пошуку драйвера.");
        }

        string label = string.IsNullOrWhiteSpace(deviceLabel) ? deviceInstanceId : deviceLabel;

        if (isElevated())
        {
            var nativeAttempt = invokeDiInstallDevice(deviceInstanceId);
            if (nativeAttempt.Status == NativeInstallStatus.Installed)
            {
                string msg = nativeAttempt.RebootRequired
                    ? $"Драйвер для «{label}» оновлено. Потрібне перезавантаження системи."
                    : $"Пошук та оновлення драйвера для «{label}» завершено.";
                return new DriverSearchResult(
                    Succeeded: true,
                    Outcome: DriverSearchOutcome.Updated,
                    Message: msg,
                    RebootRequired: nativeAttempt.RebootRequired);
            }

            if (nativeAttempt.Status == NativeInstallStatus.Cancelled)
            {
                return new DriverSearchResult(
                    Succeeded: true,
                    Outcome: DriverSearchOutcome.WizardLaunched,
                    Message: $"Майстер оновлення драйвера для «{label}» закрито користувачем.");
            }
        }
        else if (launchElevatedDeviceProperties(deviceInstanceId))
        {
            return new DriverSearchResult(
                Succeeded: true,
                Outcome: DriverSearchOutcome.WizardLaunched,
                Message: $"Відкрито вікно властивостей і оновлення драйвера Windows для «{label}».");
        }

        // Automatic fallback: best-effort pnputil device scan + Windows Update Optional Updates
        try
        {
            runPnpUtilScanDevices();
        }
        catch
        {
            // best-effort scan
        }

        bool openedWindowsUpdate = false;
        try
        {
            openedWindowsUpdate = openWindowsUpdateOptionalUpdates();
        }
        catch
        {
            openedWindowsUpdate = false;
        }

        if (openedWindowsUpdate)
        {
            return new DriverSearchResult(
                Succeeded: true,
                Outcome: DriverSearchOutcome.FallbackWindowsUpdateOpened,
                Message: $"Запущено сканування пристроїв (pnputil) та відкрито «Необов'язкові оновлення» Windows Update для «{label}».");
        }

        return new DriverSearchResult(
            Succeeded: false,
            Outcome: DriverSearchOutcome.Failed,
            Message: $"Не вдалося запустити пошук драйверів для «{label}». Спробуйте відкрити Диспетчер пристроїв вручну.");
    }

    private static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return IsUserAnAdmin();
        }
        catch
        {
            return false;
        }
    }

    private static NativeDriverInstallAttempt TryInvokeNativeDiInstallDevice(string instanceId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NativeDriverInstallAttempt(NativeInstallStatus.Failed, false, 0);
        }

        IntPtr devInfoSet = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
        {
            return new NativeDriverInstallAttempt(NativeInstallStatus.Failed, false, Marshal.GetLastWin32Error());
        }

        try
        {
            var devInfoData = new SpDevInfoData
            {
                CbSize = (uint)Marshal.SizeOf<SpDevInfoData>()
            };

            if (!SetupDiOpenDeviceInfoW(devInfoSet, instanceId, IntPtr.Zero, 0, ref devInfoData))
            {
                return new NativeDriverInstallAttempt(NativeInstallStatus.Failed, false, Marshal.GetLastWin32Error());
            }

            bool installed = DiInstallDevice(
                IntPtr.Zero,
                devInfoSet,
                ref devInfoData,
                IntPtr.Zero,
                DiidFlagShowSearchUi,
                out int needReboot);

            if (installed)
            {
                return new NativeDriverInstallAttempt(NativeInstallStatus.Installed, needReboot != 0, 0);
            }

            int lastError = Marshal.GetLastWin32Error();
            if (lastError == ErrorCancelled)
            {
                return new NativeDriverInstallAttempt(NativeInstallStatus.Cancelled, false, lastError);
            }

            return new NativeDriverInstallAttempt(NativeInstallStatus.Failed, false, lastError);
        }
        catch
        {
            return new NativeDriverInstallAttempt(NativeInstallStatus.Failed, false, Marshal.GetLastWin32Error());
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(devInfoSet);
        }
    }

    private static bool TryLaunchElevatedDeviceProperties(string instanceId)
    {
        try
        {
            string safeInstanceId = instanceId.Replace("\"", string.Empty, StringComparison.Ordinal);
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"devmgr.dll,DeviceProperties_RunDLL /DeviceID \"{safeInstanceId}\"",
                UseShellExecute = true,
                Verb = "runas"
            });

            return proc is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void TryRunPnpUtilScanDevices()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/scan-devices",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            proc?.WaitForExit(5000);
        }
        catch
        {
            // best-effort
        }
    }

    private static bool TryOpenWindowsUpdateOptionalUpdates()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:windowsupdate-optionalupdates",
                UseShellExecute = true
            });

            return proc is not null;
        }
        catch
        {
            return false;
        }
    }

    private static HardwareInventory CollectInventory()
    {
        var os = CollectOsInfo();
        var cpu = CollectCpuSpec();
        var ram = CollectRamModules();
        var board = CollectMotherboardSpec();
        var gpus = CollectGpuSpecs();
        var nics = CollectNetworkAdapters();

        return new HardwareInventory(
            CollectedAt: DateTimeOffset.UtcNow,
            Os: os,
            Cpu: cpu,
            RamModules: ram,
            Motherboard: board,
            Gpus: gpus,
            NetworkAdapters: nics);
    }

    private static OsVersionInfo CollectOsInfo()
    {
        string edition = "Windows";
        string displayVersion = string.Empty;
        string buildNumber = Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key is not null)
                {
                    string rawProduct = (key.GetValue("ProductName") as string)?.Trim() ?? "Windows";
                    displayVersion = (key.GetValue("DisplayVersion") as string)?.Trim() ?? string.Empty;
                    string currentBuild = (key.GetValue("CurrentBuildNumber") as string)?.Trim() ?? buildNumber;
                    int ubr = key.GetValue("UBR") is int u ? u : 0;

                    if (int.TryParse(currentBuild, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedBuild) &&
                        parsedBuild >= 22000 &&
                        rawProduct.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                    {
                        rawProduct = rawProduct.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
                    }

                    edition = rawProduct;
                    buildNumber = ubr > 0 ? $"{currentBuild}.{ubr}" : currentBuild;
                }
            }
            catch
            {
                // fallback to Environment.OSVersion
            }
        }

        return new OsVersionInfo(
            Edition: edition,
            DisplayVersion: string.IsNullOrWhiteSpace(displayVersion) ? "—" : displayVersion,
            BuildNumber: buildNumber,
            Architecture: RuntimeInformation.OSArchitecture.ToString(),
            IsUefiFirmware: DetectUefiFirmware());
    }

    private static bool DetectUefiFirmware()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            if (key?.GetValue("PEFirmwareType") is int fwType)
            {
                return fwType == 2;
            }

            using var sbKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (sbKey is not null)
            {
                return true;
            }
        }
        catch
        {
            // fallback below
        }

        _ = GetFirmwareEnvironmentVariableW(string.Empty, "{00000000-0000-0000-0000-000000000000}", IntPtr.Zero, 0);
        int lastErr = Marshal.GetLastWin32Error();
        return lastErr != 1; // ERROR_INVALID_FUNCTION (1) indicates Legacy BIOS
    }

    private static CpuSpec CollectCpuSpec()
    {
        string name = "Процесор Windows";
        string manufacturer = "Unknown";
        string socket = "—";
        int physicalCores = Environment.ProcessorCount;
        int logicalProcessors = Environment.ProcessorCount;
        double maxClockMHz = 0;
        long l2Bytes = 0;
        long l3Bytes = 0;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, Manufacturer, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize FROM Win32_Processor");
                foreach (ManagementBaseObject obj in searcher.Get())
                {
                    name = NormalizeSpaces((obj["Name"] as string) ?? name);
                    manufacturer = NormalizeCpuVendor((obj["Manufacturer"] as string) ?? manufacturer);
                    socket = (obj["SocketDesignation"] as string)?.Trim() ?? "—";
                    if (obj["NumberOfCores"] is uint cores && cores > 0)
                    {
                        physicalCores = (int)cores;
                    }

                    if (obj["NumberOfLogicalProcessors"] is uint logical && logical > 0)
                    {
                        logicalProcessors = (int)logical;
                    }

                    if (obj["MaxClockSpeed"] is uint maxMhz && maxMhz > 0)
                    {
                        maxClockMHz = maxMhz;
                    }

                    if (obj["L2CacheSize"] is uint l2Kb && l2Kb > 0)
                    {
                        l2Bytes = (long)l2Kb * 1024L;
                    }

                    if (obj["L3CacheSize"] is uint l3Kb && l3Kb > 0)
                    {
                        l3Bytes = (long)l3Kb * 1024L;
                    }

                    break;
                }
            }
            catch
            {
                // WMI fallback handled
            }
        }

        return new CpuSpec(
            Name: name,
            Manufacturer: manufacturer,
            Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
            Socket: socket,
            PhysicalCores: physicalCores,
            LogicalProcessors: logicalProcessors,
            MaxClockMHz: maxClockMHz,
            L2CacheBytes: l2Bytes,
            L3CacheBytes: l3Bytes);
    }

    private static List<RamModuleSpec> CollectRamModules()
    {
        var list = new List<RamModuleSpec>();
        if (!OperatingSystem.IsWindows())
        {
            return list;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                string locator = (obj["DeviceLocator"] as string)?.Trim() ?? string.Empty;
                string bank = (obj["BankLabel"] as string)?.Trim() ?? string.Empty;
                string slot = !string.IsNullOrWhiteSpace(locator) ? locator : (!string.IsNullOrWhiteSpace(bank) ? bank : $"Слот {list.Count + 1}");

                string rawMfg = (obj["Manufacturer"] as string)?.Trim() ?? string.Empty;
                string manufacturer = DecodeRamManufacturer(rawMfg);
                string part = NormalizeSpaces((obj["PartNumber"] as string) ?? "—");
                long capacity = obj["Capacity"] is ulong cap ? (long)cap : 0L;

                int speed = 0;
                if (obj["ConfiguredClockSpeed"] is uint cfgSpeed && cfgSpeed > 0)
                {
                    speed = (int)cfgSpeed;
                }
                else if (obj["Speed"] is uint stdSpeed && stdSpeed > 0)
                {
                    speed = (int)stdSpeed;
                }

                uint smbiosType = obj["SMBIOSMemoryType"] is uint t ? t : 0u;
                string memType = DecodeSmbiosMemoryType(smbiosType, speed);

                list.Add(new RamModuleSpec(
                    SlotLocator: slot,
                    Manufacturer: manufacturer,
                    PartNumber: string.IsNullOrWhiteSpace(part) ? "—" : part,
                    CapacityBytes: capacity,
                    SpeedMHz: speed,
                    MemoryType: memType));
            }
        }
        catch
        {
            // WMI optional
        }

        return list;
    }

    private static MotherboardSpec CollectMotherboardSpec()
    {
        string boardMfg = "Невідомо";
        string boardProduct = "Системна плата";
        string biosVendor = "Невідомо";
        string biosVersion = "—";
        string biosDate = "—";

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var boardSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
                foreach (ManagementBaseObject obj in boardSearcher.Get())
                {
                    boardMfg = NormalizeSpaces((obj["Manufacturer"] as string) ?? boardMfg);
                    boardProduct = NormalizeSpaces((obj["Product"] as string) ?? boardProduct);
                    break;
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                using var biosSearcher = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
                foreach (ManagementBaseObject obj in biosSearcher.Get())
                {
                    biosVendor = NormalizeSpaces((obj["Manufacturer"] as string) ?? biosVendor);
                    biosVersion = NormalizeSpaces((obj["SMBIOSBIOSVersion"] as string) ?? biosVersion);
                    biosDate = FormatWmiDate(obj["ReleaseDate"] as string);
                    break;
                }
            }
            catch
            {
                // ignore
            }
        }

        return new MotherboardSpec(boardMfg, boardProduct, biosVendor, biosVersion, biosDate);
    }

    private static List<GpuSpec> CollectGpuSpecs()
    {
        var list = new List<GpuSpec>();
        if (!OperatingSystem.IsWindows())
        {
            return list;
        }

        var registryVram = ReadRegistryVramMap();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterCompatibility, DriverVersion, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                string name = NormalizeSpaces((obj["Name"] as string) ?? string.Empty);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string vendor = NormalizeSpaces((obj["AdapterCompatibility"] as string) ?? "GPU");
                string driver = NormalizeSpaces((obj["DriverVersion"] as string) ?? "—");
                string pnpId = NormalizeSpaces((obj["PNPDeviceID"] as string) ?? "—");

                long vram = 0;
                if (registryVram.TryGetValue(name, out long regBytes) && regBytes > 0)
                {
                    vram = regBytes;
                }
                else if (obj["AdapterRAM"] is uint rawRam && rawRam > 0)
                {
                    vram = rawRam;
                }

                list.Add(new GpuSpec(name, vendor, driver, vram, pnpId));
            }
        }
        catch
        {
            // ignore
        }

        return list;
    }

    private static List<NetworkAdapterSpec> CollectNetworkAdapters()
    {
        var list = new List<NetworkAdapterSpec>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                byte[] macBytes = nic.GetPhysicalAddress().GetAddressBytes();
                if (macBytes.Length == 0)
                {
                    continue;
                }

                string mac = string.Join(':', macBytes.Select(static b => b.ToString("X2", CultureInfo.InvariantCulture)));
                bool connected = nic.OperationalStatus == OperationalStatus.Up;
                long speed = nic.Speed > 0 ? nic.Speed : 0;
                string typeLabel = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi 802.11",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                    _ => nic.NetworkInterfaceType.ToString()
                };

                string displayName = !string.IsNullOrWhiteSpace(nic.Description) ? nic.Description.Trim() : nic.Name.Trim();
                if (IsVirtualOrFilterNetworkAdapter(displayName))
                {
                    continue;
                }

                list.Add(new NetworkAdapterSpec(displayName, mac, speed, connected, typeLabel));
            }

            list.Sort(static (a, b) => b.IsConnected.CompareTo(a.IsConnected));
        }
        catch
        {
            // ignore
        }

        return list;
    }

    private static IReadOnlyList<DeviceNode> EnumerateSetupApiDevices()
    {
        var results = new List<DeviceNode>();
        if (!OperatingSystem.IsWindows())
        {
            return results;
        }

        IntPtr devInfoSet = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
        {
            return results;
        }

        try
        {
            using var classRootKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
            var driverCache = new Dictionary<string, (string Version, string? Date, string? Provider)>(StringComparer.OrdinalIgnoreCase);

            uint index = 0;
            while (true)
            {
                var devInfoData = new SpDevInfoData
                {
                    CbSize = (uint)Marshal.SizeOf<SpDevInfoData>()
                };

                if (!SetupDiEnumDeviceInfo(devInfoSet, index, ref devInfoData))
                {
                    break;
                }

                index++;

                string instanceId = GetDeviceInstanceId(devInfoData.DevInst);
                if (string.IsNullOrWhiteSpace(instanceId) || IsNoiseSoftwareDevice(instanceId))
                {
                    continue;
                }

                string? friendly = GetDeviceStringProperty(devInfoSet, ref devInfoData, SpdrpFriendlyName);
                string? desc = GetDeviceStringProperty(devInfoSet, ref devInfoData, SpdrpDeviceDesc);
                string displayName = !string.IsNullOrWhiteSpace(friendly)
                    ? friendly.Trim()
                    : (!string.IsNullOrWhiteSpace(desc) ? desc.Trim() : instanceId);

                string rawClass = GetDeviceStringProperty(devInfoSet, ref devInfoData, SpdrpClass)?.Trim() ?? string.Empty;
                string rawEnumerator = GetDeviceStringProperty(devInfoSet, ref devInfoData, SpdrpEnumeratorName)?.Trim() ?? string.Empty;
                string rawMfg = GetDeviceStringProperty(devInfoSet, ref devInfoData, SpdrpMfg)?.Trim() ?? string.Empty;
                string? driverKeyRel = GetDeviceStringProperty(devInfoSet, ref devInfoData, SpdrpDriver)?.Trim();

                string driverVersion = "Вбудований Windows";
                string? driverDate = null;
                if (!string.IsNullOrWhiteSpace(driverKeyRel))
                {
                    if (!driverCache.TryGetValue(driverKeyRel, out var cachedDriver))
                    {
                        cachedDriver = ReadDriverRegistryInfo(classRootKey, driverKeyRel);
                        driverCache[driverKeyRel] = cachedDriver;
                    }

                    if (!string.IsNullOrWhiteSpace(cachedDriver.Version))
                    {
                        driverVersion = cachedDriver.Version;
                    }

                    driverDate = cachedDriver.Date;
                    if (string.IsNullOrWhiteSpace(rawMfg) && !string.IsNullOrWhiteSpace(cachedDriver.Provider))
                    {
                        rawMfg = cachedDriver.Provider;
                    }
                }

                var (status, problemCode) = ReadDevNodeStatus(devInfoData.DevInst);
                string? parentId = GetParentDeviceInstanceId(devInfoData.DevInst);
                string busType = ClassifyBusType(instanceId, rawEnumerator, rawClass);
                string category = LocalizeDeviceCategory(rawClass, busType);
                string manufacturer = NormalizeManufacturer(rawMfg);

                results.Add(new DeviceNode(
                    InstanceId: instanceId,
                    FriendlyName: displayName,
                    Category: category,
                    BusType: busType,
                    Manufacturer: manufacturer,
                    DriverVersion: driverVersion,
                    DriverDate: driverDate,
                    Status: status,
                    ProblemCode: problemCode,
                    IsPresent: true,
                    ParentInstanceId: parentId));
            }
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        results.Sort(static (a, b) =>
        {
            int statusOrderA = a.Status is DeviceStatus.Error or DeviceStatus.Warning ? 0 : (a.Status == DeviceStatus.Disabled ? 1 : 2);
            int statusOrderB = b.Status is DeviceStatus.Error or DeviceStatus.Warning ? 0 : (b.Status == DeviceStatus.Disabled ? 1 : 2);
            int cmp = statusOrderA.CompareTo(statusOrderB);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = string.Compare(a.Category, b.Category, StringComparison.CurrentCulture);
            return cmp != 0 ? cmp : string.Compare(a.FriendlyName, b.FriendlyName, StringComparison.CurrentCultureIgnoreCase);
        });

        return results;
    }

    internal static string DecodeSmbiosMemoryType(uint code, int speedMHz) => code switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => speedMHz >= 4400 ? "DDR5" : (speedMHz >= 2133 ? "DDR4" : (speedMHz >= 1066 ? "DDR3" : "RAM"))
    };

    internal static string DecodeRamManufacturer(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Невідомо";
        }

        string clean = raw.Trim();
        return clean.ToUpperInvariant() switch
        {
            "8502" or "0402" => "Patriot Memory",
            "80AD" or "00AD" => "SK Hynix",
            "80CE" or "00CE" => "Samsung",
            "802C" or "002C" => "Micron",
            "04CD" or "84CD" => "G.Skill",
            "029E" or "829E" => "Corsair",
            "0198" or "8198" => "Kingston",
            "059B" or "859B" => "Crucial",
            "04CB" or "84CB" => "ADATA",
            "04EF" or "84EF" => "TeamGroup",
            _ => clean
        };
    }

    internal static string FormatWmiDate(string? dmtfDate)
    {
        if (string.IsNullOrWhiteSpace(dmtfDate))
        {
            return "—";
        }

        string trimmed = dmtfDate.Trim();
        if (trimmed.Length >= 8 &&
            DateTime.TryParseExact(trimmed[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fallbackDt))
        {
            return fallbackDt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return trimmed;
    }

    internal static string ClassifyBusType(string instanceId, string enumeratorName, string deviceClass)
    {
        string prefix = !string.IsNullOrWhiteSpace(enumeratorName)
            ? enumeratorName
            : (instanceId.Contains('\\') ? instanceId[..instanceId.IndexOf('\\')] : instanceId);

        if (prefix.Equals("PCI", StringComparison.OrdinalIgnoreCase))
        {
            return "PCIe";
        }

        if (prefix.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
            prefix.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase))
        {
            return "USB";
        }

        if (prefix.Equals("SCSI", StringComparison.OrdinalIgnoreCase) ||
            prefix.Equals("STORAGE", StringComparison.OrdinalIgnoreCase) ||
            prefix.Equals("NVME", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase))
        {
            return "Диски";
        }

        if (prefix.Equals("HDAUDIO", StringComparison.OrdinalIgnoreCase) ||
            prefix.Equals("MMDEVAPI", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("Media", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("AudioEndpoint", StringComparison.OrdinalIgnoreCase))
        {
            return "Аудіо";
        }

        if (prefix.Equals("HID", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("HIDClass", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase))
        {
            return "HID";
        }

        if (deviceClass.Equals("Net", StringComparison.OrdinalIgnoreCase))
        {
            return "Мережа";
        }

        if (prefix.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("Display", StringComparison.OrdinalIgnoreCase) ||
            deviceClass.Equals("Monitor", StringComparison.OrdinalIgnoreCase))
        {
            return "Дисплей";
        }

        if (prefix.Equals("ACPI", StringComparison.OrdinalIgnoreCase) ||
            prefix.Equals("ACPI_HAL", StringComparison.OrdinalIgnoreCase))
        {
            return "ACPI";
        }

        return string.IsNullOrWhiteSpace(prefix) ? "Система" : prefix.ToUpperInvariant();
    }

    internal static string LocalizeDeviceCategory(string rawClass, string busType) => rawClass.ToUpperInvariant() switch
    {
        "DISPLAY" => "Відеоадаптери",
        "MONITOR" => "Монітори",
        "PROCESSOR" => "Процесори",
        "DISKDRIVE" or "STORAGEVOLUME" or "SCSIADAPTER" or "HDC" => "Дискові накопичувачі",
        "USB" or "USBDEVICE" => "Контролери та пристрої USB",
        "NET" => "Мережеві адаптери",
        "MEDIA" or "AUDIOENDPOINT" => "Аудіо та мультимедіа",
        "KEYBOARD" or "MOUSE" or "HIDCLASS" => "Пристрої введення (HID)",
        "BLUETOOTH" => "Bluetooth",
        "SYSTEM" => "Системні компоненти",
        _ => busType switch
        {
            "USB" => "Контролери та пристрої USB",
            "Диски" => "Дискові накопичувачі",
            "Аудіо" => "Аудіо та мультимедіа",
            "Мережа" => "Мережеві адаптери",
            "HID" => "Пристрої введення (HID)",
            _ => "Системні компоненти"
        }
    };

    private static bool IsVirtualOrFilterNetworkAdapter(string description)
    {
        return description.Contains("Filter Driver", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("LightWeight Filter", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("QoS Packet Scheduler", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase) ||
               description.Contains("Npcap", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNoiseSoftwareDevice(string instanceId)
    {
        return instanceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) ||
               instanceId.StartsWith(@"SW\", StringComparison.OrdinalIgnoreCase) ||
               instanceId.StartsWith(@"HTREE\", StringComparison.OrdinalIgnoreCase) ||
               instanceId.StartsWith(@"ROOT\VOLMGR", StringComparison.OrdinalIgnoreCase) ||
               instanceId.StartsWith(@"ROOT\LEGACY_", StringComparison.OrdinalIgnoreCase) ||
               instanceId.StartsWith(@"STORAGE\VOLUME", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeManufacturer(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            raw.Contains("Стандартні", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Standard", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Generic", StringComparison.OrdinalIgnoreCase))
        {
            return "Стандартний драйвер Windows";
        }

        return raw.Trim('(', ')');
    }

    private static string NormalizeCpuVendor(string raw) => raw.Trim() switch
    {
        "GenuineIntel" => "Intel Corporation",
        "AuthenticAMD" => "AMD",
        var other => other
    };

    private static (string Version, string? Date, string? Provider) ReadDriverRegistryInfo(RegistryKey? classRootKey, string relativePath)
    {
        if (classRootKey is null)
        {
            return (string.Empty, null, null);
        }

        try
        {
            using var sub = classRootKey.OpenSubKey(relativePath);
            if (sub is null)
            {
                return (string.Empty, null, null);
            }

            string version = (sub.GetValue("DriverVersion") as string)?.Trim() ?? string.Empty;
            string? date = (sub.GetValue("DriverDate") as string)?.Trim();
            string? provider = (sub.GetValue("ProviderName") as string)?.Trim();
            return (version, date, provider);
        }
        catch
        {
            return (string.Empty, null, null);
        }
    }

    private static (DeviceStatus Status, int ProblemCode) ReadDevNodeStatus(uint devInst)
    {
        if (CM_Get_DevNode_Status(out uint statusFlags, out uint problemNumber, devInst, 0) != CrSuccess)
        {
            return (DeviceStatus.Unknown, 0);
        }

        if (problemNumber == CmProbDisabled)
        {
            return (DeviceStatus.Disabled, (int)problemNumber);
        }

        if ((statusFlags & DnHasProblem) != 0 || problemNumber != 0)
        {
            return (DeviceStatus.Warning, (int)problemNumber);
        }

        return (DeviceStatus.Ok, 0);
    }

    private static unsafe string GetDeviceInstanceId(uint devInst)
    {
        char* buffer = stackalloc char[260];
        int res = CM_Get_Device_IDW(devInst, buffer, 260, 0);
        return res == CrSuccess ? new string(buffer).Trim() : string.Empty;
    }

    private static string? GetParentDeviceInstanceId(uint devInst)
    {
        if (CM_Get_Parent(out uint parentInst, devInst, 0) != CrSuccess || parentInst == 0)
        {
            return null;
        }

        string id = GetDeviceInstanceId(parentInst);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private static unsafe string? GetDeviceStringProperty(IntPtr devInfoSet, ref SpDevInfoData devInfoData, uint property)
    {
        byte* buffer = stackalloc byte[520];
        if (!SetupDiGetDeviceRegistryPropertyW(
            devInfoSet,
            ref devInfoData,
            property,
            out _,
            buffer,
            516,
            out uint requiredSize) || requiredSize <= 2)
        {
            return null;
        }

        int charCount = Math.Min(256, (int)(requiredSize / 2));
        string str = Encoding.Unicode.GetString(buffer, charCount * 2).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(str) ? null : str;
    }

    private static Dictionary<string, long> ReadRegistryVramMap()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null)
            {
                return map;
            }

            foreach (string subName in classKey.GetSubKeyNames())
            {
                if (subName.Length != 4 || !int.TryParse(subName, out _))
                {
                    continue;
                }

                using var sub = classKey.OpenSubKey(subName);
                string? desc = sub?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(desc))
                {
                    continue;
                }

                object? qwMem = sub?.GetValue("HardwareInformation.qwMemorySize");
                if (qwMem is long l && l > 0)
                {
                    map[desc.Trim()] = l;
                }
                else if (qwMem is byte[] bytes && bytes.Length >= 8)
                {
                    long val = BitConverter.ToInt64(bytes, 0);
                    if (val > 0)
                    {
                        map[desc.Trim()] = val;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return map;
    }

    private static string NormalizeSpaces(string input) =>
        string.Join(' ', input.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevInfoData deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte* propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial IntPtr SetupDiCreateDeviceInfoList(
        IntPtr classGuid,
        IntPtr hwndParent);

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfoW(
        IntPtr deviceInfoSet,
        string deviceInstanceId,
        IntPtr hwndParent,
        uint openFlags,
        ref SpDevInfoData deviceInfoData);

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiInstallDevice(
        IntPtr hwndParent,
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        IntPtr driverInfoData,
        uint flags,
        out int needReboot);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsUserAnAdmin();

    [LibraryImport("CfgMgr32.dll")]
    private static partial int CM_Get_DevNode_Status(
        out uint ulStatus,
        out uint ulProblemNumber,
        uint dnDevInst,
        uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial int CM_Get_Parent(
        out uint pdnDevInst,
        uint dnDevInst,
        uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static unsafe partial int CM_Get_Device_IDW(
        uint dnDevInst,
        char* buffer,
        int bufferLen,
        uint ulFlags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFirmwareEnvironmentVariableW(
        string lpName,
        string lpGuid,
        IntPtr pBuffer,
        uint nSize);
}
