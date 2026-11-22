using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Argus.App.Services;
using Argus.Core.Contracts.System;
using Argus.Core.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed class ActiveConnectionRowViewModel
{
    public ActiveConnectionRowViewModel(
        ActiveConnectionItem model,
        Func<ActiveConnectionItem, Task> onBlockApp)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Protocol = model.Protocol;
        LocalEndpoint = $"{model.LocalAddress}:{model.LocalPort}";
        RemoteEndpoint = model.RemotePort > 0
            ? $"{model.RemoteAddress}:{model.RemotePort}"
            : model.RemoteAddress;
        State = model.State;
        PidText = model.Pid.ToString(CultureInfo.InvariantCulture);
        ProcessName = model.ProcessName;
        ExecutablePath = string.IsNullOrWhiteSpace(model.ExecutablePath) ? "Системний / захищений процес" : model.ExecutablePath;
        HasExecutablePath = !string.IsNullOrWhiteSpace(model.ExecutablePath);
        IsEstablished = string.Equals(model.State, "ESTABLISHED", StringComparison.OrdinalIgnoreCase);

        BlockAppCommand = new AsyncRelayCommand(() => onBlockApp(model), () => HasExecutablePath);
    }

    public ActiveConnectionItem Model { get; }
    public string Protocol { get; }
    public string LocalEndpoint { get; }
    public string RemoteEndpoint { get; }
    public string State { get; }
    public string PidText { get; }
    public string ProcessName { get; }
    public string ExecutablePath { get; }
    public bool HasExecutablePath { get; }
    public bool IsEstablished { get; }
    public ICommand BlockAppCommand { get; }
}

public sealed class ProcessTrafficRowViewModel
{
    public ProcessTrafficRowViewModel(
        ProcessNetworkTrafficItem model,
        Func<string, string, Task> onBlockApp)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        PidText = model.Pid.ToString(CultureInfo.InvariantCulture);
        ProcessName = model.ProcessName;
        ExecutablePath = string.IsNullOrWhiteSpace(model.ExecutablePath) ? "Системний / захищений процес" : model.ExecutablePath;
        HasExecutablePath = !string.IsNullOrWhiteSpace(model.ExecutablePath);
        SocketsText = $"{model.ActiveConnectionCount} з'єдн.";
        IsExactEtw = model.SourceKind == NetworkTrafficSourceKind.NativeEtwKernel;

        DownloadSpeedText = FormatSpeed(model.DownloadBytesPerSec);
        UploadSpeedText = IsExactEtw ? FormatSpeed(model.UploadBytesPerSec) : "—";
        TotalVolumeText = IsExactEtw
            ? $"↓ {FormatVolume(model.TotalReceivedBytes)} · ↑ {FormatVolume(model.TotalSentBytes)}"
            : $"≈ {FormatVolume(model.TotalReceivedBytes)} (I/O)";

        HasActiveTraffic = model.TotalBytesPerSec >= 512.0;

        BlockAppCommand = new AsyncRelayCommand(
            () => onBlockApp(model.ProcessName, model.ExecutablePath ?? string.Empty),
            () => HasExecutablePath);
    }

    public ProcessNetworkTrafficItem Model { get; }
    public string PidText { get; }
    public string ProcessName { get; }
    public string ExecutablePath { get; }
    public bool HasExecutablePath { get; }
    public string SocketsText { get; }
    public bool IsExactEtw { get; }
    public string DownloadSpeedText { get; }
    public string UploadSpeedText { get; }
    public string TotalVolumeText { get; }
    public bool HasActiveTraffic { get; }
    public ICommand BlockAppCommand { get; }

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

    public FirewallRuleItem Model { get; }
    public string Name { get; }
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
    private readonly Action<Action> _uiDispatch;
    private readonly Timer _pollTimer;
    private IReadOnlyList<ActiveConnectionItem> _allConnections = [];
    private IReadOnlyList<ProcessNetworkTrafficItem> _allTrafficProcesses = [];
    private IReadOnlyList<FirewallRuleItem> _allFirewallRules = [];
    private int _refreshGate;
    private volatile bool _isUiActive;
    private bool _initialized;

    public NetworkViewModel(
        INetworkManagerService networkService,
        Action<Action>? uiDispatch = null,
        AppUiModeService? uiMode = null,
        bool autoLoadOnInit = false,
        IProcessNetworkTrafficService? trafficService = null)
        : base(
            ArgusSectionId.Network,
            "Мережа та Windows Firewall",
            "Активні TCP/UDP-з'єднання, живий облік трафіку по процесах (ETW) і правила брандмауера з префіксом ARGUS_",
            uiMode,
            uiDispatch)
    {
        _networkService = networkService ?? throw new ArgumentNullException(nameof(networkService));
        _trafficService = trafficService;
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
    private string _connectionFilter = "Усі";

    [ObservableProperty]
    private string _firewallFilter = "Лише ARGUS_";

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

    partial void OnConnectionCountChanged(int value) => OnPropertyChanged(nameof(ConnectionsTabLabel));
    partial void OnTrafficProcessCountChanged(int value) => OnPropertyChanged(nameof(TrafficTabLabel));
    partial void OnFirewallRuleCountChanged(int value) => OnPropertyChanged(nameof(FirewallTabLabel));
    partial void OnArgusRuleCountChanged(int value) => OnPropertyChanged(nameof(FirewallTabLabel));
    partial void OnSearchQueryChanged(string value) => ApplyFilters();
    partial void OnConnectionFilterChanged(string value) => ApplyFilters();
    partial void OnFirewallFilterChanged(string value) => ApplyFilters();

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
        ConnectionFilter = string.IsNullOrWhiteSpace(filter) ? "Усі" : filter;
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
        return HandleBlockAppByPathAsync(connection.ProcessName, connection.ExecutablePath ?? string.Empty);
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

        // Filter Connections
        var filteredConns = _allConnections.Where(c =>
        {
            if (!IsExpertMode && c.IsLoopback)
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
                   c.LocalPort.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   c.RemotePort.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   c.Pid.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase);
        }).Take(200).ToList();

        Connections.Clear();
        foreach (var conn in filteredConns)
        {
            Connections.Add(new ActiveConnectionRowViewModel(conn, HandleBlockApplicationAsync));
        }

        // Filter Process Traffic
        var filteredTraffic = _allTrafficProcesses.Where(p =>
        {
            if (string.IsNullOrEmpty(q))
            {
                return true;
            }

            return p.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   p.Pid.ToString(CultureInfo.InvariantCulture).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   (p.ExecutablePath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        }).Take(120).ToList();

        ProcessTraffic.Clear();
        foreach (var item in filteredTraffic)
        {
            ProcessTraffic.Add(new ProcessTrafficRowViewModel(item, HandleBlockAppByPathAsync));
        }

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
        }).Take(150).ToList();

        FirewallRules.Clear();
        foreach (var rule in filteredRules)
        {
            FirewallRules.Add(new FirewallRuleRowViewModel(rule, HandleToggleFirewallRuleAsync, HandleDeleteFirewallRuleAsync));
        }

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
