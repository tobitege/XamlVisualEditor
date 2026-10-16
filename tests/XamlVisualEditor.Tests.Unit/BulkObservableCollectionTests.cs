using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using XamlVisualEditor.PropertyEditorExtension;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_RaisesSingleResetAndReplacesContent()
    {
        BulkObservableCollection<int> collection = new() { 1, 2, 3 };
        List<NotifyCollectionChangedAction> actions = new();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);

        collection.ReplaceAll(new[] { 4, 5 });

        Assert.Equal(new[] { 4, 5 }, collection);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, actions);
    }

    [Fact]
    public void ReplaceAll_WithEmptySource_ClearsCollection()
    {
        BulkObservableCollection<string> collection = new() { "a" };

        collection.ReplaceAll(System.Array.Empty<string>());

        Assert.Empty(collection);
    }

    [Fact]
    public void InsertRange_And_RemoveRange_RaiseOneNotificationWithAllItems()
    {
        BulkObservableCollection<int> collection = new() { 1, 5 };
        List<NotifyCollectionChangedEventArgs> changes = new();
        collection.CollectionChanged += (_, e) => changes.Add(e);

        collection.InsertRange(1, new[] { 2, 3, 4 });
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, collection);
        NotifyCollectionChangedEventArgs added = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
        Assert.Equal(1, added.NewStartingIndex);
        Assert.Equal(new[] { 2, 3, 4 }, added.NewItems!.Cast<int>());

        changes.Clear();
        collection.RemoveRange(1, 3);
        Assert.Equal(new[] { 1, 5 }, collection);
        NotifyCollectionChangedEventArgs removed = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Remove, removed.Action);
        Assert.Equal(1, removed.OldStartingIndex);
        Assert.Equal(new[] { 2, 3, 4 }, removed.OldItems!.Cast<int>());

        changes.Clear();
        collection.InsertRange(0, System.Array.Empty<int>());
        collection.RemoveRange(0, 0);
        Assert.Empty(changes);
    }
}
