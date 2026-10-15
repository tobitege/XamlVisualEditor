using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Recycling;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Lets the dock views of a closing window go before the shell tears the dock model down.
/// </summary>
/// <remarks>
/// Saving the layout on shutdown detaches owners, factories and documents from the dockables, and clearing the
/// window data context takes the layout away from the dock control. Views stay bound after they leave the visual
/// tree, so each of these steps would log a binding error for every tab, pin button and document still showing a
/// dockable, including the views of hidden tabs in the view cache. The dock control keeps its data context, and
/// the views inside it drop theirs: a binding without a data context stays silent.
/// </remarks>
public static class DockViewRelease
{
    /// <summary>Releases the dock views of the window from their dockables.</summary>
    public static void Release(Window window)
    {
        foreach (DockControl dockControl in window.GetVisualDescendants().OfType<DockControl>().ToArray())
        {
            // Turns the inherited data context into a local value, so clearing the window data context later does not
            // take the layout away from the dock control.
            dockControl.DataContext = dockControl.DataContext;
            List<Visual> views = new(dockControl.GetVisualDescendants());
            if (ControlRecyclingDataTemplate.GetControlRecycling(dockControl) is ControlRecycling recycling)
            {
                foreach (IDockable dockable in EnumerateDockables(dockControl.Layout))
                {
                    if (recycling.TryGetValue(dockable, out object? cached) && cached is Visual cachedView)
                    {
                        views.Add(cachedView);
                        views.AddRange(cachedView.GetVisualDescendants());
                    }
                }
            }

            // Only the dock controls let go; the views they host keep their documents and tools, whose bindings stay valid.
            foreach (StyledElement view in Enumerable.Reverse(views).OfType<StyledElement>()
                         .Where(view => view.GetType().Assembly == typeof(DockControl).Assembly))
            {
                view.DataContext = null;
            }
        }
    }

    private static IEnumerable<IDockable> EnumerateDockables(IDockable? root)
    {
        Stack<IDockable> pending = new();
        HashSet<IDockable> visited = new(ReferenceEqualityComparer.Instance);
        if (root is not null)
        {
            pending.Push(root);
        }

        while (pending.TryPop(out IDockable? dockable))
        {
            if (!visited.Add(dockable))
            {
                continue;
            }

            yield return dockable;
            IEnumerable<IDockable>? children = dockable switch
            {
                IRootDock rootDock => (rootDock.VisibleDockables ?? Enumerable.Empty<IDockable>())
                    .Concat(rootDock.HiddenDockables ?? Enumerable.Empty<IDockable>())
                    .Concat(rootDock.LeftPinnedDockables ?? Enumerable.Empty<IDockable>())
                    .Concat(rootDock.RightPinnedDockables ?? Enumerable.Empty<IDockable>())
                    .Concat(rootDock.TopPinnedDockables ?? Enumerable.Empty<IDockable>())
                    .Concat(rootDock.BottomPinnedDockables ?? Enumerable.Empty<IDockable>())
                    .Concat(rootDock.PinnedDock is { } pinnedDock ? [pinnedDock] : Enumerable.Empty<IDockable>()),
                IDock dock => dock.VisibleDockables,
                _ => null
            };
            foreach (IDockable child in children ?? Enumerable.Empty<IDockable>())
            {
                pending.Push(child);
            }
        }
    }
}
