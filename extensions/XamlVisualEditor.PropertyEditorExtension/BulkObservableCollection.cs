using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace XamlVisualEditor.PropertyEditorExtension;

/// <summary>
/// Observable collection with a bulk replace that raises a single reset
/// notification and range inserts and removals that raise a single add or remove
/// notification. Replacing a large entry set item by item makes a listening
/// collection view process every insert separately, which is quadratic overall
/// and freezes the UI thread for large property sets.
/// </summary>
internal sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (T item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Inserts the items at <paramref name="index" /> with a single add notification.</summary>
    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        CheckReentrancy();
        if (items.Count == 0) return;
        for (int offset = 0; offset < items.Count; offset++)
        {
            Items.Insert(index + offset, items[offset]);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, new List<T>(items), index));
    }

    /// <summary>Removes <paramref name="count" /> items from <paramref name="index" /> with a single remove notification.</summary>
    public void RemoveRange(int index, int count)
    {
        CheckReentrancy();
        if (count == 0) return;
        List<T> removed = new(count);
        for (int offset = 0; offset < count; offset++)
        {
            removed.Add(Items[index]);
            Items.RemoveAt(index);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, removed, index));
    }
}
