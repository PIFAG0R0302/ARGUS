using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Argus.App.Services;
using Argus.Core.Contracts.System;
using Argus.Core.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed partial class ActiveConnectionRowViewModel : ObservableObject
{
    public ActiveConnectionRowViewModel(
        ActiveConnectionItem model,
        Func<ActiveConnectionItem, Task> onBlockAppOrIp,
        Func<ActiveConnectionItem, Task> onBlockRemoteIp,
        Action<ActiveConnectionItem> onRequestTerminate)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        ConnectionKey = BuildKey(model);
        Protocol = model.Protocol;
        LocalEndpoint = $"{model.LocalAddress}:{model.LocalPort}";
        RemoteEndpoint = model.RemotePort > 0
            ? $"{model.RemoteAddress}:{model.RemotePort}"
            : model.RemoteAddress;
        PidText = model.Pid.ToString(CultureInfo.InvariantCulture);

        _state = model.State;
        _processName = model.ProcessName;
        _executablePath = FormatExecutablePath(model.ExecutablePath);
        _hasExecutablePath = !string.IsNullOrWhiteSpace(model.ExecutablePath);
        _hasBlockableRemoteIp = ConnectionHeuristicAnalyzer.IsExternalPublicIp(model.RemoteAddress);
        _isEstablished = string.Equals(model.State, "ESTABLISHED", StringComparison.OrdinalIgnoreCase);
        _suspicionLevel = model.SuspicionLevel;
        _suspicionSummary = model.SuspicionSummary ?? string.Empty;
        _canTerminateProcess = !CriticalProcessProtection.IsCriticalProcess(model.Pid, model.ProcessName, Environment.ProcessId);

        BlockAppCommand = new AsyncRelayCommand(() => onBlockAppOrIp(Model), () => CanBlock);
        BlockRemoteIpCommand = new AsyncRelayCommand(() => onBlockRemoteIp(Model), () => HasBlockableRemoteIp);
        RequestTerminateCommand = new RelayCommand(() => onRequestTerminate(Model), () => CanTerminateProcess);
    }

    public ActiveConnectionItem Model { get; private set; }
    public string ConnectionKey { get; }
    public string Protocol { get; }
    public string LocalEndpoint { get; }
    public string RemoteEndpoint { get; }
    public string PidText { get; }

    [ObservableProperty]
    private string _state;

    [ObservableProperty]
    private string _processName;

    [ObservableProperty]
    private string _executablePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBlock))]
    [NotifyPropertyChangedFor(nameof(BlockButtonLabel))]
    private bool _hasExecutablePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBlock))]
    private bool _hasBlockableRemoteIp;

    [ObservableProperty]
    private bool _isEstablished;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSuspicious))]
    [NotifyPropertyChangedFor(nameof(IsWarning))]
    [NotifyPropertyChangedFor(nameof(IsFlagged))]
    [NotifyPropertyChangedFor(nameof(SuspicionBadgeText))]
    private ConnectionSuspicionLevel _suspicionLevel;

    [ObservableProperty]
    private string _suspicionSummary;

    [ObservableProperty]
    private bool _canTerminateProcess;

    public bool CanBlock => HasExecutablePath || HasBlockableRemoteIp;
    public string BlockButtonLabel => HasExecutablePath ? "Блок .exe" : "Блок IP";
    public bool IsSuspicious => SuspicionLevel == ConnectionSuspicionLevel.Suspicious;
    public bool IsWarning => SuspicionLevel == ConnectionSuspicionLevel.Warning;
    public bool IsFlagged => SuspicionLevel != ConnectionSuspicionLevel.None;
    public string SuspicionBadgeText => SuspicionLevel switch
    {
        ConnectionSuspicionLevel.Suspicious => "Підозріле",
        ConnectionSuspicionLevel.Warning => "Увага",
        _ => string.Empty
    };

    public ICommand BlockAppCommand { get; }
    public ICommand BlockRemoteIpCommand { get; }
    public ICommand RequestTerminateCommand { get; }

    public static string BuildKey(ActiveConnectionItem m) =>
        $"{m.Protocol}|{m.LocalAddress}:{m.LocalPort}|{m.RemoteAddress}:{m.RemotePort}|{m.Pid}";

    public void UpdateFrom(ActiveConnectionItem model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        State = model.State;
        ProcessName = model.ProcessName;
        ExecutablePath = FormatExecutablePath(model.ExecutablePath);
        HasExecutablePath = !string.IsNullOrWhiteSpace(model.ExecutablePath);
        HasBlockableRemoteIp = ConnectionHeuristicAnalyzer.IsExternalPublicIp(model.RemoteAddress);
        IsEstablished = string.Equals(model.State, "ESTABLISHED", StringComparison.OrdinalIgnoreCase);
        SuspicionLevel = model.SuspicionLevel;
        SuspicionSummary = model.SuspicionSummary ?? string.Empty;
        CanTerminateProcess = !CriticalProcessProtection.IsCriticalProcess(model.Pid, model.ProcessName, Environment.ProcessId);
    }

    private static string FormatExecutablePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "Системний / захищений процес" : path;
}

public sealed partial class ProcessTrafficRowViewModel : ObservableObject
{
    public ProcessTrafficRowViewModel(
        ProcessNetworkTrafficItem model,
        Func<string, string, Task> onBlockApp)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Pid = model.Pid;
        PidText = model.Pid.ToString(CultureInfo.InvariantCulture);

        _processName = model.ProcessName;
        _executablePath = FormatExecutablePath(model.ExecutablePath);
        _hasExecutablePath = !string.IsNullOrWhiteSpace(model.ExecutablePath);
        _socketsText = $"{model.ActiveConnectionCount} з'єдн.";
        _isExactEtw = model.SourceKind == NetworkTrafficSourceKind.NativeEtwKernel;
        _downloadSpeedText = FormatSpeed(model.DownloadBytesPerSec);
        _uploadSpeedText = _isExactEtw ? FormatSpeed(model.UploadBytesPerSec) : "—";
        _totalVolumeText = FormatTotalVolume(model, _isExactEtw);
        _hasActiveTraffic = model.TotalBytesPerSec >= 512.0;

        BlockAppCommand = new AsyncRelayCommand(
            () => onBlockApp(Model.ProcessName, Model.ExecutablePath ?? string.Empty),
            () => HasExecutablePath);
    }

    public ProcessNetworkTrafficItem Model { get; private set; }
    public int Pid { get; }
    public string PidText { get; }

    [ObservableProperty]
    private string _processName;

    [ObservableProperty]
    private string _executablePath;

    [ObservableProperty]
    private bool _hasExecutablePath;

    [ObservableProperty]
    private string _socketsText;

    [ObservableProperty]
    private bool _isExactEtw;

    [ObservableProperty]
    private string _downloadSpeedText;

    [ObservableProperty]
    private string _uploadSpeedText;

    [ObservableProperty]
    private string _totalVolumeText;

    [ObservableProperty]
    private bool _hasActiveTraffic;

    public ICommand BlockAppCommand { get; }

    public void UpdateFrom(ProcessNetworkTrafficItem model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;

        bool exactEtw = model.SourceKind == NetworkTrafficSourceKind.NativeEtwKernel;
        ProcessName = model.ProcessName;
        ExecutablePath = FormatExecutablePath(model.ExecutablePath);
        HasExecutablePath = !string.IsNullOrWhiteSpace(model.ExecutablePath);
        SocketsText = $"{model.ActiveConnectionCount} з'єдн.";
        IsExactEtw = exactEtw;
        DownloadSpeedText = FormatSpeed(model.DownloadBytesPerSec);
        UploadSpeedText = exactEtw ? FormatSpeed(model.UploadBytesPerSec) : "—";
        TotalVolumeText = FormatTotalVolume(model, exactEtw);
        HasActiveTraffic = model.TotalBytesPerSec >= 512.0;
    }

    private static string FormatExecutablePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "Системний / захищений процес" : path;

    private static string FormatTotalVolume(ProcessNetworkTrafficItem model, bool exactEtw) =>
        exactEtw
            ? $"↓ {FormatVolume(model.TotalReceivedBytes)} · ↑ {FormatVolume(model.TotalSentBytes)}"
            : $"≈ {FormatVolume(model.TotalReceivedBytes)} (I/O)";

    internal static string FormatSpeed(double bytesPerSec)
    {
        double kb = bytesPerSec / 1024.0;
        if (kb >= 1024.0)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.00} МБ/с", kb / 1024.0);
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.0} КБ/с", kb);
    }

    internal static string FormatVolume(long bytes)
    {
        double kb = bytes / 1024.0;
        if (kb < 1024.0)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.0} КБ", kb);
        }

        double mb = kb / 1024.0;
        if (mb < 1024.0)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.0} МБ", mb);
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.00} ГБ", mb / 1024.0);
    }
}

public sealed partial class FirewallRuleRowViewModel : ObservableObject
{
    private readonly Func<FirewallRuleItem, bool, Task> _onToggle;
    private readonly Func<FirewallRuleItem, Task> _onDelete;

    public FirewallRuleRowViewModel(
        FirewallRuleItem model,
        Func<FirewallRuleItem, bool, Task> onToggle,
        Func<FirewallRuleItem, Task> onDelete)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _onToggle = onToggle ?? throw new ArgumentNullException(nameof(onToggle));
        _onDelete = onDelete ?? throw new ArgumentNullException(nameof(onDelete));

        Name = model.Name;
        Description = model.Description;
        HasDescription = !string.IsNullOrWhiteSpace(model.Description);
        DirectionText = model.Direction == FirewallRuleDirection.Inbound ? "Вхідне" : "Вихідне";
        ActionText = model.Action == FirewallRuleAction.Block ? "Блокувати" : "Дозволити";
        IsBlock = model.Action == FirewallRuleAction.Block;
        IsArgusManaged = model.IsArgusManaged;
        ProtocolAndPorts = $"{model.Protocol} · Лок.: {model.LocalPorts} · Відд.: {model.RemotePorts}";
        ApplicationPath = string.IsNullOrWhiteSpace(model.ApplicationPath) ? "Усі програми" : model.ApplicationPath;
        Profiles = model.Profiles;
        _isEnabled = model.Enabled;

        ToggleCommand = new AsyncRelayCommand(() => _onToggle(Model, !IsEnabled));
        DeleteCommand = new AsyncRelayCommand(() => _onDelete(Model), () => IsArgusManaged);
    }

    public FirewallRuleItem Model { get; private set; }
    public string Name { get; }

    public void UpdateFrom(FirewallRuleItem model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        IsEnabled = model.Enabled;
    }
    public string Description { get; }
    public bool HasDescription { get; }
    public string DirectionText { get; }
    public string ActionText { get; }
    public bool IsBlock { get; }
    public bool IsArgusManaged { get; }
    public string ProtocolAndPorts { get; }
    public string ApplicationPath { get; }
    public string Profiles { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(ToggleButtonLabel))]
    private bool _isEnabled;

    public string StateText => IsEnabled ? "Активне" : "Вимкнене";
    public string ToggleButtonLabel => IsEnabled ? "Вимкнути" : "Увімкнути";

    public ICommand ToggleCommand { get; }
    public ICommand DeleteCommand { get; }
}

public sealed partial class NetworkViewModel : SectionViewModelBase, IDisposable
{
    private static readonly string[] DirectionChoices = ["Вихідне (Outbound)", "Вхідне (Inbound)"];
    private static readonly string[] ActionChoices = ["Блокувати (Block)", "Дозволити (Allow)"];
    private static readonly string[] ProtocolChoices = ["ANY", "TCP", "UDP"];

    private readonly INetworkManagerService _networkService;
    private readonly IProcessNetworkTrafficService? _trafficService;
    private readonly IProcessMonitorService? _processMonitorService;
    private readonly Action<Action> _uiDispatch;
    private readonly Timer _pollTimer;
    private IReadOnlyList<ActiveConnectionItem> _allConnections = [];
    private IReadOnlyList<ProcessNetworkTrafficItem> _allTrafficProcesses = [];
    private IReadOnlyList<FirewallRuleItem> _allFirewallRules = [];
    private ActiveConnectionItem? _pendingTerminateConnection;
    private int _refreshGate;
    private volatile bool _isUiActive;
    private bool _initialized;

    public NetworkViewModel(
        INetworkManagerService networkService,
        Action<Action>? uiDispatch = null,
        AppUiModeService? uiMode = null,
        bool autoLoadOnInit = false,
        IProcessNetworkTrafficService? trafficService = null,
        IProcessMonitorService? processMonitorService = null)
        : base(
            ArgusSectionId.Network,
            "Мережа та Windows Firewall",
            "Активні TCP/UDP-з'єднання, евристика підозрілої активності, облік трафіку (ETW) і правила Firewall ARGUS_",
            uiMode,
            uiDispatch)
    {
        _networkService = networkService ?? throw new ArgumentNullException(nameof(networkService));
        _trafficService = trafficService;
        _processMonitorService = processMonitorService;
        _uiDispatch = uiDispatch ?? (static action => action());

        Connections = [];
        ProcessTraffic = [];
        FirewallRules = [];
        _pollTimer = new Timer(OnPollTick, null, Timeout.Infinite, Timeout.Infinite);

        if (autoLoadOnInit)
        {
            EnsureInitialized();
        }
    }

    public ObservableCollection<ActiveConnectionRowViewModel> Connections { get; }
    public ObservableCollection<ProcessTrafficRowViewModel> ProcessTraffic { get; }
    public ObservableCollection<FirewallRuleRowViewModel> FirewallRules { get; }

    public IReadOnlyList<string> DirectionOptions => DirectionChoices;
    public IReadOnlyList<string> ActionOptions => ActionChoices;
    public IReadOnlyList<string> ProtocolOptions => ProtocolChoices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectionsTab))]
    [NotifyPropertyChangedFor(nameof(IsTrafficTab))]
    [NotifyPropertyChangedFor(nameof(IsFirewallTab))]
    private int _activeSubTab;

    public bool IsConnectionsTab => ActiveSubTab == 0;
    public bool IsTrafficTab => ActiveSubTab == 1;
    public bool IsFirewallTab => ActiveSubTab == 2;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsConnFilterEstablished))]
    [NotifyPropertyChangedFor(nameof(IsConnFilterListening))]
    [NotifyPropertyChangedFor(nameof(IsConnFilterTcp))]
    [NotifyPropertyChangedFor(nameof(IsConnFilterUdp))]
    private string _connectionFilter = "Усі";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsConnFilterEstablished))]
    [NotifyPropertyChangedFor(nameof(IsConnFilterListening))]
    [NotifyPropertyChangedFor(nameof(IsConnFilterTcp))]
    [NotifyPropertyChangedFor(nameof(IsConnFilterUdp))]
    private bool _onlySuspiciousConnections;

    public bool IsConnFilterAll => !OnlySuspiciousConnections && ConnectionFilter == "Усі";
    public bool IsConnFilterEstablished => !OnlySuspiciousConnections && ConnectionFilter == "ESTABLISHED";
    public bool IsConnFilterListening => !OnlySuspiciousConnections && ConnectionFilter == "LISTENING";
    public bool IsConnFilterTcp => !OnlySuspiciousConnections && ConnectionFilter == "TCP";
    public bool IsConnFilterUdp => !OnlySuspiciousConnections && ConnectionFilter == "UDP";

    [ObservableProperty]
    private int _suspiciousConnectionCount;

    [ObservableProperty]
    private bool _isTerminateConfirmationOpen;

    [ObservableProperty]
    private string _terminatePromptText = string.Empty;

    [ObservableProperty]
    private string _firewallFilter = "Лише ARGUS_";

    [ObservableProperty]
    private ActiveConnectionRowViewModel? _selectedConnection;

    [ObservableProperty]
    private ProcessTrafficRowViewModel? _selectedTrafficProcess;

    [ObservableProperty]
    private FirewallRuleRowViewModel? _selectedFirewallRule;

    // Connections sorting
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnSortByProtocol))]
    [NotifyPropertyChangedFor(nameof(IsConnSortByProcess))]
    [NotifyPropertyChangedFor(nameof(IsConnSortByLocal))]
    [NotifyPropertyChangedFor(nameof(IsConnSortByRemote))]
    [NotifyPropertyChangedFor(nameof(ConnSortProtocolHeader))]
    [NotifyPropertyChangedFor(nameof(ConnSortProcessHeader))]
    [NotifyPropertyChangedFor(nameof(ConnSortLocalHeader))]
    [NotifyPropertyChangedFor(nameof(ConnSortRemoteHeader))]
    private string _connectionSortBy = "PROCESS";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnSortProtocolHeader))]
    [NotifyPropertyChangedFor(nameof(ConnSortProcessHeader))]
    [NotifyPropertyChangedFor(nameof(ConnSortLocalHeader))]
    [NotifyPropertyChangedFor(nameof(ConnSortRemoteHeader))]
    private bool _connectionSortDescending;

    public bool IsConnSortByProtocol => ConnectionSortBy == "PROTOCOL";
    public bool IsConnSortByProcess => ConnectionSortBy == "PROCESS";
    public bool IsConnSortByLocal => ConnectionSortBy == "LOCAL";
    public bool IsConnSortByRemote => ConnectionSortBy == "REMOTE";

    public string ConnSortProtocolHeader => CollectionSyncHelper.FormatSortHeader("ПРОТОКОЛ", "PROTOCOL", ConnectionSortBy, ConnectionSortDescending);
    public string ConnSortProcessHeader => CollectionSyncHelper.FormatSortHeader("ПРОЦЕС · PID · СТАН", "PROCESS", ConnectionSortBy, ConnectionSortDescending);
    public string ConnSortLocalHeader => CollectionSyncHelper.FormatSortHeader("ЛОКАЛЬНА АДРЕСА", "LOCAL", ConnectionSortBy, ConnectionSortDescending);
    public string ConnSortRemoteHeader => CollectionSyncHelper.FormatSortHeader("ВІДДАЛЕНИЙ ВУЗОЛ", "REMOTE", ConnectionSortBy, ConnectionSortDescending);

    // Per-process Traffic sorting
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrafficSortByPid))]
    [NotifyPropertyChangedFor(nameof(IsTrafficSortByName))]
    [NotifyPropertyChangedFor(nameof(IsTrafficSortByDown))]
    [NotifyPropertyChangedFor(nameof(IsTrafficSortByUp))]
    [NotifyPropertyChangedFor(nameof(IsTrafficSortByTotal))]
    [NotifyPropertyChangedFor(nameof(TrafficSortPidHeader))]
    [NotifyPropertyChangedFor(nameof(TrafficSortNameHeader))]
    [NotifyPropertyChangedFor(nameof(TrafficSortDownHeader))]
    [NotifyPropertyChangedFor(nameof(TrafficSortUpHeader))]
    [NotifyPropertyChangedFor(nameof(TrafficSortTotalHeader))]
    private string _trafficSortBy = "DOWN";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrafficSortPidHeader))]
    [NotifyPropertyChangedFor(nameof(TrafficSortNameHeader))]
    [NotifyPropertyChangedFor(nameof(TrafficSortDownHeader))]
    [NotifyPropertyChangedFor(nameof(TrafficSortUpHeader))]
    [NotifyPropertyChangedFor(nameof(TrafficSortTotalHeader))]
    private bool _trafficSortDescending = true;

    public bool IsTrafficSortByPid => TrafficSortBy == "PID";
    public bool IsTrafficSortByName => TrafficSortBy == "NAME";
    public bool IsTrafficSortByDown => TrafficSortBy == "DOWN";
    public bool IsTrafficSortByUp => TrafficSortBy == "UP";
    public bool IsTrafficSortByTotal => TrafficSortBy == "TOTAL";

    public string TrafficSortPidHeader => CollectionSyncHelper.FormatSortHeader("PID", "PID", TrafficSortBy, TrafficSortDescending);
    public string TrafficSortNameHeader => CollectionSyncHelper.FormatSortHeader("ПРОЦЕС ТА З'ЄДНАННЯ", "NAME", TrafficSortBy, TrafficSortDescending);
    public string TrafficSortDownHeader => CollectionSyncHelper.FormatSortHeader("ВХІД (↓)", "DOWN", TrafficSortBy, TrafficSortDescending);
    public string TrafficSortUpHeader => CollectionSyncHelper.FormatSortHeader("ВИХІД (↑)", "UP", TrafficSortBy, TrafficSortDescending);
    public string TrafficSortTotalHeader => CollectionSyncHelper.FormatSortHeader("УСЬОГО ЗА СЕСІЮ", "TOTAL", TrafficSortBy, TrafficSortDescending);

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _summaryText = "Відкрийте вкладку або натисніть «Оновити» для перегляду мережевих з'єднань і правил Firewall.";

    [ObservableProperty]
    private string _actionFeedback = string.Empty;

    [ObservableProperty]
    private bool _hasActionFeedback;

    [ObservableProperty]
    private int _connectionCount;

    [ObservableProperty]
    private int _trafficProcessCount;

    [ObservableProperty]
    private int _firewallRuleCount;

    [ObservableProperty]
    private int _argusRuleCount;

    [ObservableProperty]
    private bool _isEtwTrafficActive;

    [ObservableProperty]
    private string _trafficSourceSummary = "Ініціалізація лічильника мережевого трафіку…";

    [ObservableProperty]
    private string _totalTrafficRateText = "↓ 0,0 КБ/с · ↑ 0,0 КБ/с";

    // Inline New Firewall Rule Form
    [ObservableProperty]
    private bool _isNewRulePanelOpen;

    [ObservableProperty]
    private string _newRuleName = string.Empty;

    [ObservableProperty]
    private int _newRuleDirectionIndex;

    [ObservableProperty]
    private int _newRuleActionIndex;

    [ObservableProperty]
    private int _newRuleProtocolIndex;

    [ObservableProperty]
    private string _newRuleAppPath = string.Empty;

    [ObservableProperty]
    private string _newRulePorts = string.Empty;

    public string ConnectionsTabLabel => $"Активні з'єднання ({ConnectionCount})";
    public string TrafficTabLabel => $"Трафік по процесах ({TrafficProcessCount})";
    public string FirewallTabLabel => $"Правила Firewall ({ArgusRuleCount} ARGUS / {FirewallRuleCount})";
    public string SuspiciousFilterLabel => $"Лише підозрілі ({SuspiciousConnectionCount})";

    partial void OnConnectionCountChanged(int value) => OnPropertyChanged(nameof(ConnectionsTabLabel));
    partial void OnTrafficProcessCountChanged(int value) => OnPropertyChanged(nameof(TrafficTabLabel));
    partial void OnFirewallRuleCountChanged(int value) => OnPropertyChanged(nameof(FirewallTabLabel));
    partial void OnArgusRuleCountChanged(int value) => OnPropertyChanged(nameof(FirewallTabLabel));
    partial void OnSuspiciousConnectionCountChanged(int value) => OnPropertyChanged(nameof(SuspiciousFilterLabel));
    partial void OnOnlySuspiciousConnectionsChanged(bool value) => ApplyFilters();
    partial void OnSearchQueryChanged(string value) => ApplyFilters();
    partial void OnConnectionFilterChanged(string value) => ApplyFilters();
    partial void OnFirewallFilterChanged(string value) => ApplyFilters();
    partial void OnConnectionSortByChanged(string value) => ApplyFilters();
    partial void OnConnectionSortDescendingChanged(bool value) => ApplyFilters();
    partial void OnTrafficSortByChanged(string value) => ApplyFilters();
    partial void OnTrafficSortDescendingChanged(bool value) => ApplyFilters();

    [RelayCommand]
    private void SetConnectionSort(string sortKey)
    {
        if (string.IsNullOrWhiteSpace(sortKey))
        {
            return;
        }

        string normalized = sortKey.Trim().ToUpperInvariant();
        if (string.Equals(ConnectionSortBy, normalized, StringComparison.Ordinal))
        {
            ConnectionSortDescending = !ConnectionSortDescending;
        }
        else
        {
            ConnectionSortDescending = false;
            ConnectionSortBy = normalized;
        }
    }

    [RelayCommand]
    private void SetTrafficSort(string sortKey)
    {
        if (string.IsNullOrWhiteSpace(sortKey))
        {
            return;
        }

        string normalized = sortKey.Trim().ToUpperInvariant();
        if (string.Equals(TrafficSortBy, normalized, StringComparison.Ordinal))
        {
            TrafficSortDescending = !TrafficSortDescending;
        }
        else
        {
            TrafficSortDescending = normalized is not ("NAME" or "PID");
            TrafficSortBy = normalized;
        }
    }

    public void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _ = RefreshAsync();
    }

    public void SetUiActive(bool active)
    {
        if (_isUiActive == active)
        {
            return;
        }

        _isUiActive = active;
        if (active)
        {
            _trafficService?.Start();
            EnsureInitialized();
            _pollTimer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
        else
        {
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _trafficService?.Stop();
        }
    }

    protected override void OnExpertModeChanged(bool isExpertMode)
    {
        if (isExpertMode && FirewallFilter == "Лише ARGUS_" && _allFirewallRules.Count > 0 && ArgusRuleCount == 0)
        {
            FirewallFilter = "Усі";
        }

        ApplyFilters();
    }

    [RelayCommand]
    private void SelectSubTab(string indexText)
    {
        if (int.TryParse(indexText, out int idx) && idx is 0 or 1 or 2)
        {
            ActiveSubTab = idx;
            ApplyFilters();
        }
    }

    [RelayCommand]
    private void SetConnectionFilter(string filter)
    {
        OnlySuspiciousConnections = false;
        ConnectionFilter = string.IsNullOrWhiteSpace(filter) ? "Усі" : filter;
    }

    [RelayCommand]
    private void ToggleOnlySuspicious()
    {
        OnlySuspiciousConnections = !OnlySuspiciousConnections;
    }

    [RelayCommand]
    public async Task ConfirmTerminateConnectionProcessAsync()
    {
        var target = _pendingTerminateConnection;
        _pendingTerminateConnection = null;
        IsTerminateConfirmationOpen = false;
        TerminatePromptText = string.Empty;

        if (target is null)
        {
            return;
        }

        if (_processMonitorService is null)
        {
            ActionFeedback = "Служба керування процесами не підключена.";
            HasActionFeedback = true;
            return;
        }

        var result = await _processMonitorService.TerminateProcessAsync(target.Pid).ConfigureAwait(false);
        _uiDispatch(() =>
        {
            ActionFeedback = result.Message;
            HasActionFeedback = true;
        });

        if (result.Succeeded)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private void CancelTerminateConnectionProcess()
    {
        _pendingTerminateConnection = null;
        IsTerminateConfirmationOpen = false;
        TerminatePromptText = string.Empty;
    }

    [RelayCommand]
    private void SetFirewallFilter(string filter)
    {
        FirewallFilter = string.IsNullOrWhiteSpace(filter) ? "Усі" : filter;
    }

    [RelayCommand]
    private void ToggleNewRulePanel()
    {
        IsNewRulePanelOpen = !IsNewRulePanelOpen;
    }

    [RelayCommand]
    private void ResetTrafficTotals()
    {
        _trafficService?.ResetTotals();
        ActionFeedback = "Лічильники мережевого трафіку по процесах скинуто.";
        HasActionFeedback = true;
        _ = RefreshTrafficOnlyAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshGate, 1) == 1)
        {
            return;
        }

        _uiDispatch(() => IsLoading = true);

        try
        {
            var connTask = _networkService.GetActiveConnectionsAsync();
            var fwTask = _networkService.GetFirewallRulesAsync(argusOnly: false);

            await Task.WhenAll(connTask, fwTask).ConfigureAwait(false);

            var connections = await connTask.ConfigureAwait(false);
            var rules = await fwTask.ConfigureAwait(false);

            NetworkTrafficSnapshot? trafficSnap = _trafficService is not null
                ? await _trafficService.GetSnapshotAsync(connections).ConfigureAwait(false)
                : null;

            _allConnections = connections;
            _allFirewallRules = rules;
            _allTrafficProcesses = trafficSnap?.Processes ?? [];

            _uiDispatch(() =>
            {
                ConnectionCount = _allConnections.Count;
                FirewallRuleCount = _allFirewallRules.Count;
                ArgusRuleCount = _allFirewallRules.Count(static r => r.IsArgusManaged);
                ApplyTrafficSnapshotMetadata(trafficSnap);
                ApplyFilters();
            });
        }
        catch (Exception ex)
        {
            _uiDispatch(() =>
            {
                ActionFeedback = $"Помилка читання мережі/Firewall: {ex.Message}";
                HasActionFeedback = true;
            });
        }
        finally
        {
            _uiDispatch(() => IsLoading = false);
            Volatile.Write(ref _refreshGate, 0);
        }
    }

    private async Task RefreshTrafficOnlyAsync()
    {
        if (Interlocked.Exchange(ref _refreshGate, 1) == 1)
        {
            return;
        }

        try
        {
            var connections = await _networkService.GetActiveConnectionsAsync().ConfigureAwait(false);
            NetworkTrafficSnapshot? trafficSnap = _trafficService is not null
                ? await _trafficService.GetSnapshotAsync(connections).ConfigureAwait(false)
                : null;

            _allConnections = connections;
            _allTrafficProcesses = trafficSnap?.Processes ?? [];

            _uiDispatch(() =>
            {
                ConnectionCount = _allConnections.Count;
                ApplyTrafficSnapshotMetadata(trafficSnap);
                ApplyFilters();
            });
        }
        catch
        {
            // Keep UI quiet on transient poll glitch
        }
        finally
        {
            Volatile.Write(ref _refreshGate, 0);
        }
    }

    private void ApplyTrafficSnapshotMetadata(NetworkTrafficSnapshot? snap)
    {
        if (snap is null)
        {
            TrafficProcessCount = 0;
            IsEtwTrafficActive = false;
            TrafficSourceSummary = "Служба обліку трафіку не підключена.";
            TotalTrafficRateText = "↓ 0,0 КБ/с · ↑ 0,0 КБ/с";
            return;
        }

        TrafficProcessCount = snap.Processes.Count;
        IsEtwTrafficActive = snap.IsEtwActive;
        TrafficSourceSummary = snap.SourceSummary;

        double totalDown = snap.Processes.Sum(static p => p.DownloadBytesPerSec);
        double totalUp = snap.Processes.Sum(static p => p.UploadBytesPerSec);
        TotalTrafficRateText = snap.IsEtwActive
            ? $"↓ {ProcessTrafficRowViewModel.FormatSpeed(totalDown)} · ↑ {ProcessTrafficRowViewModel.FormatSpeed(totalUp)}"
            : $"≈ {ProcessTrafficRowViewModel.FormatSpeed(totalDown)} (оцінка I/O)";
    }

    [RelayCommand]
    public async Task CreateNewRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(NewRuleName))
        {
            ActionFeedback = "Вкажіть назву правила (префікс ARGUS_ додається автоматично).";
            HasActionFeedback = true;
            return;
        }

        string protocol = ProtocolChoices[Math.Clamp(NewRuleProtocolIndex, 0, ProtocolChoices.Length - 1)];
        string? ports = string.IsNullOrWhiteSpace(NewRulePorts) ? null : NewRulePorts.Trim();
        if (ports is not null && string.Equals(protocol, "ANY", StringComparison.OrdinalIgnoreCase))
        {
            protocol = "TCP";
        }

        var direction = NewRuleDirectionIndex == 1 ? FirewallRuleDirection.Inbound : FirewallRuleDirection.Outbound;
        var action = NewRuleActionIndex == 1 ? FirewallRuleAction.Allow : FirewallRuleAction.Block;

        var request = new FirewallRuleCreateRequest(
            Name: ArgusFirewallNaming.EnsurePrefix(NewRuleName),
            Direction: direction,
            Action: action,
            ApplicationPath: string.IsNullOrWhiteSpace(NewRuleAppPath) ? null : NewRuleAppPath.Trim(),
            Protocol: protocol,
            LocalPorts: direction == FirewallRuleDirection.Inbound ? ports : null,
            RemotePorts: direction == FirewallRuleDirection.Outbound ? ports : null,
            Description: "Створено в ARGUS (Мережа v2)");

        var result = await _networkService.CreateFirewallRuleAsync(request).ConfigureAwait(false);
        _uiDispatch(() =>
        {
            ActionFeedback = result.Message;
            HasActionFeedback = true;
            if (result.Succeeded)
            {
                NewRuleName = string.Empty;
                NewRuleAppPath = string.Empty;
                NewRulePorts = string.Empty;
                IsNewRulePanelOpen = false;
            }
        });

        if (result.Succeeded)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private Task HandleBlockApplicationAsync(ActiveConnectionItem connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.ExecutablePath))
        {
            return HandleBlockAppByPathAsync(connection.ProcessName, connection.ExecutablePath);
        }

        return HandleBlockRemoteIpAsync(connection);
    }

    private async Task HandleBlockRemoteIpAsync(ActiveConnectionItem connection)
    {
        var result = await _networkService.BlockRemoteIpAsync(
            connection.RemoteAddress,
            reason: connection.SuspicionSummary ?? connection.ProcessName,
            direction: FirewallRuleDirection.Outbound).ConfigureAwait(false);

        _uiDispatch(() =>
        {
            ActionFeedback = result.Message;
            HasActionFeedback = true;
        });

        if (result.Succeeded)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private void RequestTerminateConnectionProcess(ActiveConnectionItem connection)
    {
        if (CriticalProcessProtection.IsCriticalProcess(connection.Pid, connection.ProcessName, Environment.ProcessId))
        {
            ActionFeedback = $"Процес «{connection.ProcessName}» (PID {connection.Pid}) захищений ОС від завершення.";
            HasActionFeedback = true;
            return;
        }

        _pendingTerminateConnection = connection;
        string reasonPart = string.IsNullOrWhiteSpace(connection.SuspicionSummary)
            ? string.Empty
            : $" Причина: {connection.SuspicionSummary}.";
        TerminatePromptText = $"Завершити процес «{connection.ProcessName}» (PID {connection.Pid})?{reasonPart} Незбережені дані програми буде втрачено.";
        IsTerminateConfirmationOpen = true;
    }

    private async Task HandleBlockAppByPathAsync(string processName, string executablePath)
    {
        var result = await _networkService.BlockApplicationAsync(
            processName,
            executablePath,
            FirewallRuleDirection.Outbound).ConfigureAwait(false);

        _uiDispatch(() =>
        {
            ActionFeedback = result.Message;
            HasActionFeedback = true;
        });

        if (result.Succeeded)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleToggleFirewallRuleAsync(FirewallRuleItem rule, bool enabled)
    {
        var result = await _networkService.SetFirewallRuleEnabledAsync(rule.Name, enabled).ConfigureAwait(false);
        _uiDispatch(() =>
        {
            ActionFeedback = result.Message;
            HasActionFeedback = true;
        });

        if (result.Succeeded)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleDeleteFirewallRuleAsync(FirewallRuleItem rule)
    {
        var result = await _networkService.DeleteArgusFirewallRuleAsync(rule.Name).ConfigureAwait(false);
        _uiDispatch(() =>
        {
            ActionFeedback = result.Message;
            HasActionFeedback = true;
        });

        if (result.Succeeded)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    private void OnPollTick(object? state)
    {
        if (_isUiActive && !IsFirewallTab)
        {
            _ = RefreshTrafficOnlyAsync();
        }
    }

    private void ApplyFilters()
    {
        string q = (SearchQuery ?? string.Empty).Trim();

        SuspiciousConnectionCount = _allConnections.Count(static c => c.IsFlagged);

        var connQuery = _allConnections.Where(c =>
        {
            if (OnlySuspiciousConnections && !c.IsFlagged)
            {
                return false;
            }

            if (!IsExpertMode && !OnlySuspiciousConnections && c.IsLoopback)
            {
                return false;
            }

            bool kindMatch = ConnectionFilter switch
            {
                "ESTABLISHED" => string.Equals(c.State, "ESTABLISHED", StringComparison.OrdinalIgnoreCase),
                "LISTENING" => string.Equals(c.State, "LISTENING", StringComparison.OrdinalIgnoreCase),
                "TCP" => c.Protocol.StartsWith("TCP", StringComparison.OrdinalIgnoreCase),
                "UDP" => c.Protocol.StartsWith("UDP", StringComparison.OrdinalIgnoreCase),
                _ => true
            };

            if (!kindMatch)
            {
                return false;
            }

            if (string.IsNullOrEmpty(q))
            {
                return true;
            }

            return c.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   c.LocalAddress.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   c.RemoteAddress.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   (c.SuspicionSummary?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   c.LocalPort.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   c.RemotePort.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   c.Pid.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase);
        });

        connQuery = (ConnectionSortBy, ConnectionSortDescending) switch
        {
            ("PROTOCOL", true) => connQuery
                .OrderByDescending(static c => c.Protocol, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static c => c.LocalPort)
                .ThenBy(static c => c.Pid),
            ("PROTOCOL", false) => connQuery
                .OrderBy(static c => c.Protocol, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static c => c.LocalPort)
                .ThenBy(static c => c.Pid),
            ("LOCAL", true) => connQuery
                .OrderByDescending(static c => c.LocalAddress, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(static c => c.LocalPort)
                .ThenBy(static c => c.Pid),
            ("LOCAL", false) => connQuery
                .OrderBy(static c => c.LocalAddress, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static c => c.LocalPort)
                .ThenBy(static c => c.Pid),
            ("REMOTE", true) => connQuery
                .OrderByDescending(static c => c.RemoteAddress, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(static c => c.RemotePort)
                .ThenBy(static c => c.Pid),
            ("REMOTE", false) => connQuery
                .OrderBy(static c => c.RemoteAddress, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static c => c.RemotePort)
                .ThenBy(static c => c.Pid),
            (_, true) => connQuery
                .OrderByDescending(static c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static c => c.Pid)
                .ThenBy(static c => c.Protocol, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static c => c.LocalPort)
                .ThenBy(static c => c.RemotePort),
            _ => connQuery
                .OrderBy(static c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static c => c.Pid)
                .ThenBy(static c => c.Protocol, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static c => c.LocalPort)
                .ThenBy(static c => c.RemotePort)
        };

        var filteredConns = connQuery.Take(200).ToList();
        CollectionSyncHelper.SyncByKey(
            Connections,
            filteredConns,
            ActiveConnectionRowViewModel.BuildKey,
            static r => r.ConnectionKey,
            m => new ActiveConnectionRowViewModel(
                m,
                HandleBlockApplicationAsync,
                HandleBlockRemoteIpAsync,
                RequestTerminateConnectionProcess),
            static (r, m) => r.UpdateFrom(m),
            SelectedConnection,
            row => SelectedConnection = row,
            StringComparer.Ordinal);

        // Filter & Sort Process Traffic
        var trafficQuery = _allTrafficProcesses.Where(p =>
        {
            if (string.IsNullOrEmpty(q))
            {
                return true;
            }

            return p.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   p.Pid.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   (p.ExecutablePath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        });

        trafficQuery = (TrafficSortBy, TrafficSortDescending) switch
        {
            ("PID", true) => trafficQuery.OrderByDescending(static p => p.Pid),
            ("PID", false) => trafficQuery.OrderBy(static p => p.Pid),
            ("NAME", true) => trafficQuery
                .OrderByDescending(static p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static p => p.Pid),
            ("NAME", false) => trafficQuery
                .OrderBy(static p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static p => p.Pid),
            ("UP", true) => trafficQuery
                .OrderByDescending(static p => p.UploadBytesPerSec)
                .ThenByDescending(static p => p.DownloadBytesPerSec)
                .ThenBy(static p => p.Pid),
            ("UP", false) => trafficQuery
                .OrderBy(static p => p.UploadBytesPerSec)
                .ThenBy(static p => p.DownloadBytesPerSec)
                .ThenBy(static p => p.Pid),
            ("TOTAL", true) => trafficQuery
                .OrderByDescending(static p => p.TotalReceivedBytes + p.TotalSentBytes)
                .ThenByDescending(static p => p.TotalBytesPerSec)
                .ThenBy(static p => p.Pid),
            ("TOTAL", false) => trafficQuery
                .OrderBy(static p => p.TotalReceivedBytes + p.TotalSentBytes)
                .ThenBy(static p => p.TotalBytesPerSec)
                .ThenBy(static p => p.Pid),
            (_, false) => trafficQuery
                .OrderBy(static p => p.DownloadBytesPerSec)
                .ThenBy(static p => p.UploadBytesPerSec)
                .ThenBy(static p => p.Pid),
            _ => trafficQuery
                .OrderByDescending(static p => p.DownloadBytesPerSec)
                .ThenByDescending(static p => p.UploadBytesPerSec)
                .ThenByDescending(static p => p.TotalReceivedBytes + p.TotalSentBytes)
                .ThenBy(static p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static p => p.Pid)
        };

        var filteredTraffic = trafficQuery.Take(120).ToList();
        CollectionSyncHelper.SyncByKey(
            ProcessTraffic,
            filteredTraffic,
            static m => m.Pid,
            static r => r.Pid,
            m => new ProcessTrafficRowViewModel(m, HandleBlockAppByPathAsync),
            static (r, m) => r.UpdateFrom(m),
            SelectedTrafficProcess,
            row => SelectedTrafficProcess = row);

        // Filter Firewall Rules
        var filteredRules = _allFirewallRules.Where(r =>
        {
            bool filterMatch = FirewallFilter switch
            {
                "Лише ARGUS_" => r.IsArgusManaged,
                "Блокування" => r.Action == FirewallRuleAction.Block,
                "Дозвіл" => r.Action == FirewallRuleAction.Allow,
                "Вхідні" => r.Direction == FirewallRuleDirection.Inbound,
                "Вихідні" => r.Direction == FirewallRuleDirection.Outbound,
                _ => true
            };

            if (!filterMatch)
            {
                return false;
            }

            if (string.IsNullOrEmpty(q))
            {
                return true;
            }

            return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   r.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   (r.ApplicationPath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   r.LocalPorts.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   r.RemotePorts.Contains(q, StringComparison.OrdinalIgnoreCase);
        }).OrderBy(static r => r.Name, StringComparer.OrdinalIgnoreCase).Take(150).ToList();

        CollectionSyncHelper.SyncByKey(
            FirewallRules,
            filteredRules,
            static m => m.Name,
            static r => r.Name,
            m => new FirewallRuleRowViewModel(m, HandleToggleFirewallRuleAsync, HandleDeleteFirewallRuleAsync),
            static (r, m) => r.UpdateFrom(m),
            SelectedFirewallRule,
            row => SelectedFirewallRule = row,
            StringComparer.OrdinalIgnoreCase);

        int establishedCount = _allConnections.Count(static c => string.Equals(c.State, "ESTABLISHED", StringComparison.OrdinalIgnoreCase));
        SummaryText = ActiveSubTab switch
        {
            0 => $"Показано {Connections.Count} із {_allConnections.Count} сокетів (ESTABLISHED: {establishedCount}){(IsExpertMode ? " · включно з loopback" : " · без loopback")}",
            1 => $"Процесів із мережевою активністю: {ProcessTraffic.Count} · Сумарно: {TotalTrafficRateText}",
            _ => $"Показано {FirewallRules.Count} із {_allFirewallRules.Count} правил Windows Firewall · Правил ARGUS_: {ArgusRuleCount}"
        };
    }

    public void Dispose()
    {
        _pollTimer.Dispose();
        _trafficService?.Stop();
        DetachUiMode();
    }
}
