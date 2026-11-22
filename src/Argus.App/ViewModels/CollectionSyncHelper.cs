using System.Collections.ObjectModel;

namespace Argus.App.ViewModels;

/// <summary>
/// Synchronizes an <see cref="ObservableCollection{T}"/> in-place by stable item key,
/// updating existing row view-models, removing disappeared ones, and moving/inserting
/// only where order actually changed so selection and scroll position stay intact.
/// </summary>
internal static class CollectionSyncHelper
{
    public static string FormatSortHeader(string label, string columnKey, string activeSortKey, bool sortDescending)
    {
        if (!string.Equals(columnKey, activeSortKey, StringComparison.OrdinalIgnoreCase))
        {
            return label;
        }

        return sortDescending ? $"{label} ▼" : $"{label} ▲";
    }

    public static void SyncByKey<TRow, TModel, TKey>(
        ObservableCollection<TRow> target,
        IReadOnlyList<TModel> desiredModels,
        Func<TModel, TKey> modelKeySelector,
        Func<TRow, TKey> rowKeySelector,
        Func<TModel, TRow> createRow,
        Action<TRow, TModel> updateRow,
        TRow? currentSelection,
        Action<TRow?> setSelection,
        IEqualityComparer<TKey>? comparer = null)
        where TRow : class
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desiredModels);
        ArgumentNullException.ThrowIfNull(modelKeySelector);
        ArgumentNullException.ThrowIfNull(rowKeySelector);
        ArgumentNullException.ThrowIfNull(createRow);
        ArgumentNullException.ThrowIfNull(updateRow);
        ArgumentNullException.ThrowIfNull(setSelection);

        comparer ??= EqualityComparer<TKey>.Default;

        bool hasSelectedKey = currentSelection is not null;
        TKey selectedKey = hasSelectedKey ? rowKeySelector(currentSelection!) : default!;

        // Bucket existing rows by key (supports rare duplicate keys without throwing)
        var existingByKey = new Dictionary<TKey, Queue<TRow>>(comparer);
        foreach (TRow row in target)
        {
            TKey key = rowKeySelector(row);
            if (!existingByKey.TryGetValue(key, out var queue))
            {
                queue = new Queue<TRow>();
                existingByKey[key] = queue;
            }

            queue.Enqueue(row);
        }

        var desiredRows = new List<TRow>(desiredModels.Count);
        var desiredSet = new HashSet<TRow>(ReferenceEqualityComparer.Instance);
        TRow? preservedSelection = null;

        foreach (TModel model in desiredModels)
        {
            TKey key = modelKeySelector(model);
            TRow row;
            if (existingByKey.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                row = queue.Dequeue();
                updateRow(row, model);
            }
            else
            {
                row = createRow(model);
            }

            desiredRows.Add(row);
            desiredSet.Add(row);

            if (hasSelectedKey && preservedSelection is null && comparer.Equals(key, selectedKey))
            {
                preservedSelection = row;
            }
        }

        // 1. Remove rows that are no longer present
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        // 2. Reconcile order in-place via Move / Insert
        for (int i = 0; i < desiredRows.Count; i++)
        {
            TRow desired = desiredRows[i];
            if (i < target.Count && ReferenceEquals(target[i], desired))
            {
                continue;
            }

            int existingIdx = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], desired))
                {
                    existingIdx = j;
                    break;
                }
            }

            if (existingIdx >= 0)
            {
                target.Move(existingIdx, i);
            }
            else
            {
                target.Insert(i, desired);
            }
        }

        if (!ReferenceEquals(currentSelection, preservedSelection))
        {
            setSelection(preservedSelection);
        }
    }
}
