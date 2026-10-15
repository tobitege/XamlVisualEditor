using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Dock.Controls.ProportionalStackPanel;

namespace XamlVisualEditor.App.Services;

/// <summary>
/// Marks dock splitters that their panel gives no room because the dock next to them is collapsed or hidden.
/// Such a splitter resizes nothing, so it keeps no idle color.
/// </summary>
/// <remarks>
/// A splitter with a fixed thickness keeps that size even in a slot without room, so the size of the presenter
/// that hosts it is observed, and only while the splitter is loaded.
/// </remarks>
internal static class DockSplitterAppearance
{
    public const string CollapsedClass = "collapsed";
    private static readonly ConditionalWeakTable<ProportionalStackPanelSplitter, IDisposable> Subscriptions = new();
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        Control.LoadedEvent.AddClassHandler<ProportionalStackPanelSplitter>(static (splitter, _) => Track(splitter));
        Control.UnloadedEvent.AddClassHandler<ProportionalStackPanelSplitter>(static (splitter, _) => Untrack(splitter));
    }

    private static void Track(ProportionalStackPanelSplitter splitter)
    {
        Untrack(splitter);
        if (splitter.GetVisualParent() is not ContentPresenter presenter) return;
        Subscriptions.Add(splitter, presenter.GetObservable(Visual.BoundsProperty).Subscribe(bounds =>
            splitter.Classes.Set(CollapsedClass, bounds.Width <= 0 || bounds.Height <= 0)));
    }

    private static void Untrack(ProportionalStackPanelSplitter splitter)
    {
        if (Subscriptions.TryGetValue(splitter, out IDisposable? subscription))
        {
            subscription.Dispose();
            Subscriptions.Remove(splitter);
        }
    }
}
