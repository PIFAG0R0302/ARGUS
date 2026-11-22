using Argus.App.Services;
using Argus.Core.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public abstract partial class SectionViewModelBase : ObservableObject
{
    private AppUiModeService? _uiMode;
    private Action<Action> _uiModeDispatch = static action => action();
    private bool _localExpertMode;

    protected SectionViewModelBase(
        ArgusSectionId sectionId,
        string title,
        string subtitle,
        AppUiModeService? uiMode = null,
        Action<Action>? uiDispatch = null)
    {
        SectionId = sectionId;
        Title = title;
        Subtitle = subtitle;
        AttachUiMode(uiMode, uiDispatch);
    }

    public ArgusSectionId SectionId { get; }
    public string Title { get; }
    public string Subtitle { get; }

    protected virtual bool FallbackIsAdministrator => false;

    public bool IsAdministrator => _uiMode?.IsAdministrator ?? FallbackIsAdministrator;

    public virtual bool NeedsElevation => !IsAdministrator;

    public bool IsExpertMode
    {
        get => _uiMode?.IsExpertMode ?? _localExpertMode;
        set
        {
            if (_uiMode is not null)
            {
                if (_uiMode.IsExpertMode != value)
                {
                    _uiMode.IsExpertMode = value;
                }

                return;
            }

            if (_localExpertMode == value)
            {
                return;
            }

            _localExpertMode = value;
            OnPropertyChanged();
            OnExpertModeChanged(value);
        }
    }

    [RelayCommand]
    private void RequestElevation()
    {
        _uiMode?.RequestElevationForSection(SectionId);
    }

    protected void AttachUiMode(AppUiModeService? uiMode, Action<Action>? uiDispatch = null)
    {
        if (ReferenceEquals(_uiMode, uiMode))
        {
            return;
        }

        if (_uiMode is not null)
        {
            _uiMode.ExpertModeChanged -= HandleExpertModeChanged;
        }

        _uiMode = uiMode;
        _uiModeDispatch = uiDispatch ?? (static action => action());

        if (_uiMode is not null)
        {
            _uiMode.ExpertModeChanged += HandleExpertModeChanged;
        }

        OnPropertyChanged(nameof(IsExpertMode));
        OnPropertyChanged(nameof(IsAdministrator));
        OnPropertyChanged(nameof(NeedsElevation));
    }

    protected void DetachUiMode()
    {
        if (_uiMode is not null)
        {
            _uiMode.ExpertModeChanged -= HandleExpertModeChanged;
            _uiMode = null;
        }
    }

    protected virtual void OnExpertModeChanged(bool isExpertMode)
    {
    }

    private void HandleExpertModeChanged(bool isExpertMode)
    {
        _uiModeDispatch(() =>
        {
            OnPropertyChanged(nameof(IsExpertMode));
            OnExpertModeChanged(isExpertMode);
        });
    }
}
