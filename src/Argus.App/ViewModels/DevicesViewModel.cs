using System.Collections.ObjectModel;
using System.Globalization;
using Argus.Core.Contracts.Devices;
using Argus.Core.Events;
using Argus.Core.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public enum DeviceFilterMode
{
    All,
    Pcie,
    Usb,
    Storage,
    NetAudio,
    Problems
}

public sealed class RamSlotRowItem
{
    public RamSlotRowItem(RamModuleSpec spec)
    {
        SlotLocator = spec.SlotLocator;
        double gib = spec.CapacityBytes / (1024.0 * 1024.0 * 1024.0);
        string speedPart = spec.SpeedMHz > 0 ? $" @ {spec.SpeedMHz} МГц" : string.Empty;
        CapacityAndSpeed = string.Format(CultureInfo.CurrentCulture, "{0:0.#} ГіБ {1}{2}", gib, spec.MemoryType, speedPart);
        VendorAndPart = $"{spec.Manufacturer} · {spec.PartNumber}";
    }

    public string SlotLocator { get; }
    public string CapacityAndSpeed { get; }
    public string VendorAndPart { get; }
}

public sealed class DeviceNodeRowItem
{
    public DeviceNodeRowItem(DeviceNode node)
    {
        Node = node;
        FriendlyName = node.FriendlyName;
        CategoryAndManufacturer = $"{node.Category} · {node.Manufacturer}";
        BusType = node.BusType;
        DriverSummary = string.IsNullOrWhiteSpace(node.DriverDate)
            ? $"Драйвер: {node.DriverVersion}"
            : $"Драйвер: {node.DriverVersion} ({node.DriverDate})";
        InstanceId = node.InstanceId;

        IsOk = node.Status == DeviceStatus.Ok;
        IsProblem = node.Status is DeviceStatus.Warning or DeviceStatus.Error;
        StatusLabel = node.Status switch
        {
            DeviceStatus.Ok => "OK",
            DeviceStatus.Disabled => "Вимкнено",
            DeviceStatus.Warning or DeviceStatus.Error => node.ProblemCode > 0 ? $"Код {node.ProblemCode}" : "Увага",
            _ => "Невідомо"
        };
    }

    public DeviceNode Node { get; }
    public string FriendlyName { get; }
    public string CategoryAndManufacturer { get; }
    public string BusType { get; }
    public string DriverSummary { get; }
    public string InstanceId { get; }
    public string StatusLabel { get; }
    public bool IsOk { get; }
    public bool IsProblem { get; }
}

public sealed partial class DevicesViewModel : SectionViewModelBase, IDisposable
{
    private readonly IDeviceProvider? _deviceProvider;
    private readonly Action<Action> _uiDispatch;
    private readonly IDisposable? _topologySubscription;
    private int _initialized;
    private IReadOnlyList<DeviceNode> _allDevices = [];

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private DeviceFilterMode _activeFilter = DeviceFilterMode.All;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _pnpMonitorStatusText = "Монітор CfgMgr32 / WM_DEVICECHANGE активний";

    [ObservableProperty]
    private string _showingCountText = "Завантаження дерева пристроїв…";

    // Top Card 1: CPU & Cache
    [ObservableProperty]
    private string _cpuTitle = "Визначення процесора…";

    [ObservableProperty]
    private string _cpuTopologyAndClockText = "—";

    [ObservableProperty]
    private string _cpuCacheAndArchText = "—";

    // Top Card 2: Motherboard & UEFI/BIOS
    [ObservableProperty]
    private string _boardTitle = "Визначення системної плати…";

    [ObservableProperty]
    private string _biosSummaryText = "—";

    [ObservableProperty]
    private string _osAndFirmwareText = "—";

    // Top Card 3: RAM Modules (SMBIOS)
    [ObservableProperty]
    private string _ramSummaryTitle = "Опитування слотів SMBIOS…";

    // Top Card 4: GPU & Network
    [ObservableProperty]
    private string _primaryGpuTitle = "Визначення відеоадаптера…";

    [ObservableProperty]
    private string _primaryGpuDriverAndIdText = "—";

    [ObservableProperty]
    private string _primaryNicSummaryText = "—";

    // Dynamic Filter Labels
    [ObservableProperty]
    private string _filterAllLabel = "Усі (0)";

    [ObservableProperty]
    private string _filterPcieLabel = "PCIe (0)";

    [ObservableProperty]
    private string _filterUsbLabel = "USB (0)";

    [ObservableProperty]
    private string _filterStorageLabel = "Диски (0)";

    [ObservableProperty]
    private string _filterNetAudioLabel = "Мережа/Аудіо (0)";

    [ObservableProperty]
    private string _filterProblemsLabel = "Проблемні (0)";

    public DevicesViewModel(
        IDeviceProvider? deviceProvider = null,
        IArgusEventBus? eventBus = null,
        Action<Action>? uiDispatch = null,
        bool autoLoadOnInit = true)
        : base(
            ArgusSectionId.Devices,
            "Пристрої та конфігурація",
            "Специфікація CPU/RAM/BIOS/GPU, живе дерево пристроїв SetupAPI та події підключення")
    {
        _deviceProvider = deviceProvider;
        _uiDispatch = uiDispatch ?? (static action => action());

        RamSlots = [];
        FilteredDevices = [];

        if (eventBus is not null)
        {
            _topologySubscription = eventBus.Subscribe<DeviceTopologyChangedEvent>(e => _uiDispatch(() => OnTopologyChanged(e)));
        }

        if (autoLoadOnInit && _deviceProvider is not null)
        {
            EnsureInitialized();
        }
    }

    public void EnsureInitialized()
    {
        if (_deviceProvider is not null && Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            _ = RefreshDevicesAsync();
        }
    }

    public ObservableCollection<RamSlotRowItem> RamSlots { get; }
    public ObservableCollection<DeviceNodeRowItem> FilteredDevices { get; }

    public bool IsFilterAll => ActiveFilter == DeviceFilterMode.All;
    public bool IsFilterPcie => ActiveFilter == DeviceFilterMode.Pcie;
    public bool IsFilterUsb => ActiveFilter == DeviceFilterMode.Usb;
    public bool IsFilterStorage => ActiveFilter == DeviceFilterMode.Storage;
    public bool IsFilterNetAudio => ActiveFilter == DeviceFilterMode.NetAudio;
    public bool IsFilterProblems => ActiveFilter == DeviceFilterMode.Problems;

    partial void OnSearchQueryChanged(string value)
    {
        ApplyDeviceFilter();
    }

    [RelayCommand]
    private void FilterAll() => SetFilterMode(DeviceFilterMode.All);

    [RelayCommand]
    private void FilterPcie() => SetFilterMode(DeviceFilterMode.Pcie);

    [RelayCommand]
    private void FilterUsb() => SetFilterMode(DeviceFilterMode.Usb);

    [RelayCommand]
    private void FilterStorage() => SetFilterMode(DeviceFilterMode.Storage);

    [RelayCommand]
    private void FilterNetAudio() => SetFilterMode(DeviceFilterMode.NetAudio);

    [RelayCommand]
    private void FilterProblems() => SetFilterMode(DeviceFilterMode.Problems);

    public void SetFilterMode(DeviceFilterMode mode)
    {
        ActiveFilter = mode;
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterPcie));
        OnPropertyChanged(nameof(IsFilterUsb));
        OnPropertyChanged(nameof(IsFilterStorage));
        OnPropertyChanged(nameof(IsFilterNetAudio));
        OnPropertyChanged(nameof(IsFilterProblems));
        ApplyDeviceFilter();
    }

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        if (_deviceProvider is null || IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var inventoryTask = _deviceProvider.GetInventoryAsync();
            var devicesTask = _deviceProvider.GetDevicesAsync();
            await Task.WhenAll(inventoryTask, devicesTask).ConfigureAwait(false);

            var inventory = await inventoryTask.ConfigureAwait(false);
            var devices = await devicesTask.ConfigureAwait(false);

            _uiDispatch(() =>
            {
                ApplyInventory(inventory);
                _allDevices = devices;
                UpdateFilterCounts(devices);
                ApplyDeviceFilter();
            });
        }
        finally
        {
            _uiDispatch(() => IsLoading = false);
        }
    }

    private void OnTopologyChanged(DeviceTopologyChangedEvent evt)
    {
        string kindText = evt.Kind switch
        {
            DeviceChangeKind.Arrived => "Підключено пристрій",
            DeviceChangeKind.Removed => "Відключено пристрій",
            _ => "Змінено дерево PnP"
        };

        PnpMonitorStatusText = $"Подія PnP ({evt.Timestamp.ToLocalTime():HH:mm:ss}): {kindText}";
        _ = RefreshDevicesAsync();
    }

    private void ApplyInventory(HardwareInventory inv)
    {
        // 1. CPU
        CpuTitle = inv.Cpu.Name;
        string clockStr = inv.Cpu.MaxClockMHz > 0
            ? string.Format(CultureInfo.CurrentCulture, "до {0:0} МГц", inv.Cpu.MaxClockMHz)
            : "частота авто";
        CpuTopologyAndClockText = $"{inv.Cpu.PhysicalCores} фіз. ядер / {inv.Cpu.LogicalProcessors} потоків · {clockStr} · Сокет {inv.Cpu.Socket}";

        double l2Mb = inv.Cpu.L2CacheBytes / (1024.0 * 1024.0);
        double l3Mb = inv.Cpu.L3CacheBytes / (1024.0 * 1024.0);
        CpuCacheAndArchText = string.Format(
            CultureInfo.CurrentCulture,
            "Кеш L2: {0:0.#} МБ · Кеш L3: {1:0.#} МБ · {2} ({3})",
            l2Mb,
            l3Mb,
            inv.Cpu.Manufacturer,
            inv.Cpu.Architecture);

        // 2. Motherboard & BIOS
        BoardTitle = $"{inv.Motherboard.Manufacturer} · {inv.Motherboard.Product}";
        BiosSummaryText = $"BIOS: {inv.Motherboard.BiosVendor} · Версія {inv.Motherboard.BiosVersion} ({inv.Motherboard.BiosReleaseDate})";
        string fwMode = inv.Os.IsUefiFirmware ? "UEFI" : "Legacy BIOS";
        OsAndFirmwareText = $"{inv.Os.Edition} ({inv.Os.DisplayVersion}, збірка {inv.Os.BuildNumber}) · Режим {fwMode}";

        // 3. RAM Slots
        RamSlots.Clear();
        long totalRamBytes = 0;
        foreach (var mod in inv.RamModules)
        {
            totalRamBytes += mod.CapacityBytes;
            RamSlots.Add(new RamSlotRowItem(mod));
        }

        double totalRamGiB = totalRamBytes / (1024.0 * 1024.0 * 1024.0);
        RamSummaryTitle = inv.RamModules.Count > 0
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.#} ГіБ · Слотів: {1}", totalRamGiB, inv.RamModules.Count)
            : "SMBIOS н/д";

        // 4. GPU & Network
        var gpu = inv.Gpus.FirstOrDefault();
        if (gpu is not null)
        {
            double vramGiB = gpu.DedicatedVideoMemoryBytes / (1024.0 * 1024.0 * 1024.0);
            PrimaryGpuTitle = vramGiB > 0
                ? string.Format(CultureInfo.CurrentCulture, "{0} · {1:0.#} ГіБ VRAM", gpu.Name, vramGiB)
                : gpu.Name;
            PrimaryGpuDriverAndIdText = $"Драйвер: {gpu.DriverVersion} · {gpu.PciDeviceId}";
        }
        else
        {
            PrimaryGpuTitle = "Відеоадаптер не виявлено";
            PrimaryGpuDriverAndIdText = "—";
        }

        var nic = inv.NetworkAdapters.FirstOrDefault();
        if (nic is not null)
        {
            double mbps = nic.LinkSpeedBitsPerSec > 0 ? nic.LinkSpeedBitsPerSec / 1_000_000.0 : 0;
            string speedText = mbps >= 1000
                ? string.Format(CultureInfo.CurrentCulture, "{0:0.#} Гбіт/с", mbps / 1000.0)
                : (mbps > 0 ? string.Format(CultureInfo.CurrentCulture, "{0:0} Мбіт/с", mbps) : "н/д");
            string stateText = nic.IsConnected ? "Активний" : "Відключений";
            PrimaryNicSummaryText = $"{nic.Name} ({nic.AdapterType}, {speedText}, {stateText}, MAC {nic.MacAddress})";
        }
        else
        {
            PrimaryNicSummaryText = "Мережеві інтерфейси не виявлено";
        }
    }

    private void UpdateFilterCounts(IReadOnlyList<DeviceNode> devices)
    {
        int total = devices.Count;
        int pcie = devices.Count(static d => MatchesFilterMode(d, DeviceFilterMode.Pcie));
        int usb = devices.Count(static d => MatchesFilterMode(d, DeviceFilterMode.Usb));
        int storage = devices.Count(static d => MatchesFilterMode(d, DeviceFilterMode.Storage));
        int netAudio = devices.Count(static d => MatchesFilterMode(d, DeviceFilterMode.NetAudio));
        int problems = devices.Count(static d => MatchesFilterMode(d, DeviceFilterMode.Problems));

        FilterAllLabel = $"Усі ({total})";
        FilterPcieLabel = $"PCIe ({pcie})";
        FilterUsbLabel = $"USB ({usb})";
        FilterStorageLabel = $"Диски ({storage})";
        FilterNetAudioLabel = $"Мережа/Аудіо ({netAudio})";
        FilterProblemsLabel = $"Проблемні ({problems})";
    }

    private void ApplyDeviceFilter()
    {
        string query = SearchQuery?.Trim() ?? string.Empty;
        var filtered = new List<DeviceNode>();

        foreach (var node in _allDevices)
        {
            if (!MatchesFilterMode(node, ActiveFilter))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(query) && !MatchesSearchQuery(node, query))
            {
                continue;
            }

            filtered.Add(node);
        }

        FilteredDevices.Clear();
        int limit = Math.Min(filtered.Count, 250);
        for (int i = 0; i < limit; i++)
        {
            FilteredDevices.Add(new DeviceNodeRowItem(filtered[i]));
        }

        ShowingCountText = $"Показано {FilteredDevices.Count} із {_allDevices.Count} пристроїв SetupAPI";
    }

    private static bool MatchesFilterMode(DeviceNode node, DeviceFilterMode mode) => mode switch
    {
        DeviceFilterMode.Pcie => node.BusType.Equals("PCIe", StringComparison.OrdinalIgnoreCase) ||
                                 node.BusType.Equals("Дисплей", StringComparison.OrdinalIgnoreCase),
        DeviceFilterMode.Usb => node.BusType.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                                node.BusType.Equals("HID", StringComparison.OrdinalIgnoreCase),
        DeviceFilterMode.Storage => node.BusType.Equals("Диски", StringComparison.OrdinalIgnoreCase),
        DeviceFilterMode.NetAudio => node.BusType.Equals("Мережа", StringComparison.OrdinalIgnoreCase) ||
                                     node.BusType.Equals("Аудіо", StringComparison.OrdinalIgnoreCase),
        DeviceFilterMode.Problems => node.Status != DeviceStatus.Ok,
        _ => true
    };

    private static bool MatchesSearchQuery(DeviceNode node, string query)
    {
        return node.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.Manufacturer.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.BusType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.DriverVersion.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               node.InstanceId.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _topologySubscription?.Dispose();
    }
}
