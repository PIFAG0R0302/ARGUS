using Argus.Core.Configuration;
using Argus.Core.Contracts.System;
using Argus.Core.Events;
using Argus.Core.Extensions;

namespace Argus.App.Services;

public sealed class AppUiModeService : IDisposable
{
    private readonly IArgusSettingsStore _settingsStore;
    private readonly IPrivilegeService _privilegeService;
    private readonly IArgusEventBus? _eventBus;
    private readonly Action? _requestAppExit;
    private readonly IDisposable? _settingsSubscription;
    private readonly object _sync = new();
    private bool _isExpertMode;
    private bool _suppressSettingsSync;
    private Task _lastPersistenceTask = Task.CompletedTask;

    public AppUiModeService(
        IArgusSettingsStore settingsStore,
        IPrivilegeService privilegeService,
        IArgusEventBus? eventBus = null,
        Action? requestAppExit = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _privilegeService = privilegeService ?? throw new ArgumentNullException(nameof(privilegeService));
        _eventBus = eventBus;
        _requestAppExit = requestAppExit;
        _isExpertMode = _settingsStore.Current.ExpertModeEnabled;

        if (_eventBus is not null)
        {
            _settingsSubscription = _eventBus.Subscribe<SettingsChangedEvent>(OnSettingsChanged);
        }
    }

    public event Action<bool>? ExpertModeChanged;

    public bool IsAdministrator => _privilegeService.IsAdministrator;

    public bool IsExpertMode
    {
        get
        {
            lock (_sync)
            {
                return _isExpertMode;
            }
        }
        set => SetExpertMode(value, persist: true);
    }

    internal Task LastPersistenceTask
    {
        get
        {
            lock (_sync)
            {
                return _lastPersistenceTask;
            }
        }
    }

    public Task SetExpertModeAsync(bool enabled)
    {
        SetExpertMode(enabled, persist: true);
        return LastPersistenceTask;
    }

    public void SetExpertMode(bool enabled, bool persist = true)
    {
        bool changed;
        lock (_sync)
        {
            changed = _isExpertMode != enabled;
            if (!changed)
            {
                return;
            }

            _isExpertMode = enabled;
        }

        ExpertModeChanged?.Invoke(enabled);

        if (persist)
        {
            var task = PersistExpertModeAsync(enabled);
            lock (_sync)
            {
                _lastPersistenceTask = task;
            }
        }
    }

    public bool RequestElevationForSection(ArgusSectionId sectionId)
    {
        if (IsAdministrator)
        {
            return false;
        }

        string sectionArg = sectionId.ToString().ToLowerInvariant();
        if (_privilegeService.TryRelaunchElevated(["--section", sectionArg]))
        {
            _requestAppExit?.Invoke();
            return true;
        }

        return false;
    }

    private void OnSettingsChanged(SettingsChangedEvent evt)
    {
        if (_suppressSettingsSync)
        {
            return;
        }

        SetExpertMode(evt.Settings.ExpertModeEnabled, persist: false);
    }

    private async Task PersistExpertModeAsync(bool enabled)
    {
        try
        {
            var updated = _settingsStore.Current with { ExpertModeEnabled = enabled };
            await _settingsStore.SaveAsync(updated).ConfigureAwait(false);

            if (_eventBus is not null)
            {
                _suppressSettingsSync = true;
                try
                {
                    _eventBus.Publish(new SettingsChangedEvent(_settingsStore.Current));
                }
                finally
                {
                    _suppressSettingsSync = false;
                }
            }
        }
        catch
        {
            // Keep UI responsive even if disk write fails transiently.
        }
    }

    public void Dispose()
    {
        _settingsSubscription?.Dispose();
    }
}
