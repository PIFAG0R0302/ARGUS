using System.Collections.ObjectModel;
using Argus.Core.Events;
using Argus.Core.Extensions;
using Argus.Core.Journal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Argus.App.ViewModels;

public sealed partial class JournalViewModel : SectionViewModelBase, IDisposable
{
    private readonly IOperationJournal _journal;
    private readonly Action<Action> _uiDispatch;
    private readonly IDisposable _journalSubscription;
    private IReadOnlyList<JournalRowItem> _allEntries = [];

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterInfo))]
    [NotifyPropertyChangedFor(nameof(IsFilterSuccess))]
    [NotifyPropertyChangedFor(nameof(IsFilterWarning))]
    [NotifyPropertyChangedFor(nameof(IsFilterError))]
    private string _activeSeverityFilter = "ALL";

    [ObservableProperty]
    private string _countSummaryText = "Записів: 0";

    public JournalViewModel(
        IOperationJournal journal,
        IArgusEventBus eventBus,
        Action<Action>? uiDispatch = null)
        : base(
            ArgusSectionId.Journal,
            "Журнал записів",
            "Повна історія операцій очищення, діагностики, оптимізації дисків, телеметрії та міграції CopyOS")
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        ArgumentNullException.ThrowIfNull(eventBus);
        _uiDispatch = uiDispatch ?? (static action => action());

        FilteredEntries = [];
        RefreshEntries();

        _journalSubscription = eventBus.Subscribe<OperationJournalEntryAddedEvent>(_ => _uiDispatch(RefreshEntries));
    }

    public ObservableCollection<JournalRowItem> FilteredEntries { get; }

    public bool IsFilterAll => ActiveSeverityFilter == "ALL";
    public bool IsFilterInfo => ActiveSeverityFilter == "INFO";
    public bool IsFilterSuccess => ActiveSeverityFilter == "SUCCESS";
    public bool IsFilterWarning => ActiveSeverityFilter == "WARNING";
    public bool IsFilterError => ActiveSeverityFilter == "ERROR";

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    [RelayCommand]
    public void RefreshEntries()
    {
        _allEntries = _journal.GetRecent(250)
            .Reverse()
            .Select(static e => new JournalRowItem(e))
            .ToArray();
        ApplyFilter();
    }

    [RelayCommand]
    private void SetFilterAll() => SetFilter("ALL");

    [RelayCommand]
    private void SetFilterInfo() => SetFilter("INFO");

    [RelayCommand]
    private void SetFilterSuccess() => SetFilter("SUCCESS");

    [RelayCommand]
    private void SetFilterWarning() => SetFilter("WARNING");

    [RelayCommand]
    private void SetFilterError() => SetFilter("ERROR");

    private void SetFilter(string filter)
    {
        ActiveSeverityFilter = filter;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string q = SearchQuery?.Trim() ?? string.Empty;
        IEnumerable<JournalRowItem> query = _allEntries;

        query = ActiveSeverityFilter switch
        {
            "INFO" => query.Where(static e => e.Severity == OperationSeverity.Info),
            "SUCCESS" => query.Where(static e => e.Severity == OperationSeverity.Success),
            "WARNING" => query.Where(static e => e.Severity == OperationSeverity.Warning),
            "ERROR" => query.Where(static e => e.Severity == OperationSeverity.Error),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(e =>
                e.Action.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.ModuleId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Details.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.TimeText.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var matched = query.ToArray();
        FilteredEntries.Clear();
        foreach (var item in matched)
        {
            FilteredEntries.Add(item);
        }

        CountSummaryText = $"Показано {matched.Length} із {_allEntries.Count}";
    }

    public void Dispose()
    {
        _journalSubscription.Dispose();
    }
}
