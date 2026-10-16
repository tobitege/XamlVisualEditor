using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>An observable collection whose whole content can be replaced with a single reset notification.</summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces all items; observers receive one reset instead of a notification per item.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (T item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
