using System.Threading;
using Avalonia;
using Avalonia.Controls.Presenters;
using Dock.Controls.ProportionalStackPanel;

namespace XamlVisualEditor.App.Services;

/// <summary>
/// Marks dock splitters that their panel gives no room because the dock next to them is collapsed or hidden.
/// Such a splitter resizes nothing, so it keeps no idle color.
/// </summary>
internal static class DockSplitterAppearance
{
    public const string CollapsedClass = "collapsed";
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        Visual.BoundsProperty.Changed.AddClassHandler<ContentPresenter>(static (presenter, _) =>
        {
            if (presenter.Child is ProportionalStackPanelSplitter splitter)
                splitter.Classes.Set(CollapsedClass, presenter.Bounds.Width <= 0 || presenter.Bounds.Height <= 0);
        });
    }
}
