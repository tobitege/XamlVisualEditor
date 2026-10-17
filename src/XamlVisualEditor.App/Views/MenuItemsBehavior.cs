using System;
using System.Collections;
using System.Collections.Specialized;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace XamlVisualEditor.App.Views;

/// <summary>Inserts dynamic entries through the item template next to the fixed items of a menu or context menu.</summary>
public sealed class MenuItemsBehavior : Behavior<ItemsControl>
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<MenuItemsBehavior, IEnumerable?>(nameof(Items));
    public static readonly StyledProperty<int> InsertIndexProperty =
        AvaloniaProperty.Register<MenuItemsBehavior, int>(nameof(InsertIndex));
    private readonly List<Control> _items = new();
    private INotifyCollectionChanged? _source;
    private IDisposable? _templateSubscription;

    public IEnumerable? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public int InsertIndex { get => GetValue(InsertIndexProperty); set => SetValue(InsertIndexProperty, value); }

    protected override void OnAttached()
    {
        base.OnAttached();
        RefreshSource();
        // A template from a dynamic resource arrives after the items; the items are built again with it.
        _templateSubscription = AssociatedObject?.GetObservable(ItemsControl.ItemTemplateProperty).Skip(1)
            .Subscribe(_ => Rebuild());
    }

    // The behavior is detached whenever its menu leaves the visual tree, for a context menu or submenu on every close;
    // removing the items there would change the menu while it closes. They stay and are replaced on the next attach.
    protected override void OnDetaching()
    {
        _templateSubscription?.Dispose();
        _templateSubscription = null;
        if (_source is not null) _source.CollectionChanged -= OnCollectionChanged;
        _source = null;
        base.OnDetaching();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (AssociatedObject is not null && (change.Property == ItemsProperty || change.Property == InsertIndexProperty))
            RefreshSource();
    }

    private void RefreshSource()
    {
        if (_source is not null) _source.CollectionChanged -= OnCollectionChanged;
        _source = Items as INotifyCollectionChanged;
        if (_source is not null) _source.CollectionChanged += OnCollectionChanged;
        Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => Rebuild();

    private void RemoveItems()
    {
        if (AssociatedObject is { } menu)
            foreach (Control item in _items) menu.Items.Remove(item);
        _items.Clear();
    }

    private void Rebuild()
    {
        RemoveItems();
        if (AssociatedObject is not { ItemTemplate: { } template } menu || Items is null) return;
        int index = Math.Clamp(InsertIndex, 0, menu.Items.Count);
        foreach (object entry in Items)
        {
            if (template.Build(entry) is not { } control) continue;
            control.DataContext = entry;
            _items.Add(control);
            menu.Items.Insert(index++, control);
        }
    }
}
