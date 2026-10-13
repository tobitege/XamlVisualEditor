using System;
using System.Linq;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using ReactiveUI;
using RootDock = Dock.Model.ReactiveUI.Controls.RootDock;
using ToolDock = Dock.Model.ReactiveUI.Controls.ToolDock;

namespace XamlVisualEditor.App.Views;

/// <summary>Applies the stored panel size after the expanded dock changed.</summary>
public sealed class PinnedDockLayoutBehavior : Behavior<DockControl>
{
    private IDisposable? _subscription;

    protected override void OnAttached()
    {
        base.OnAttached();
        DockControl control = AssociatedObject!;
        _subscription = Observable.Switch(control.GetObservable(DockControl.LayoutProperty)
            .Select(layout => layout is RootDock root
                ? Observable.Switch(root.WhenAnyValue(model => model.PinnedDock)
                    .Select(dock => dock is ToolDock tool
                        ? tool.WhenAnyValue(model => model.ActiveDockable)
                        : Observable.Return<IDockable?>(null)))
                : Observable.Return<IDockable?>(null)))
            .DistinctUntilChanged()
            .Subscribe(_ => Dispatcher.UIThread.Post(() =>
            {
                if (AssociatedObject == control)
                    control.GetVisualDescendants().OfType<PinnedDockControl>().FirstOrDefault()?.InvalidateMeasure();
            }, DispatcherPriority.Loaded));
    }

    protected override void OnDetaching()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.OnDetaching();
    }
}
