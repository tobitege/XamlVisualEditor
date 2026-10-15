using System.Linq;
using Avalonia.Controls;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Appends the file commands of <c>DocumentFileMenuItems</c> to the document context menus of the Dock theme. The theme
/// shares one menu per resource among all tabs, so the items are appended once at startup.
/// </summary>
internal static class DocumentTabContextMenus
{
    internal const string ItemsKey = "DocumentFileMenuItems";

    internal static readonly string[] MenuKeys = { "DocumentTabStripItemContextMenu", "MdiDocumentWindowContextMenu" };

    /// <summary>Appends the items to every document context menu the host resolves; returns the number of menus.</summary>
    public static int Extend(IResourceHost host)
    {
        int extended = 0;
        foreach (string menuKey in MenuKeys)
        {
            if (!host.TryGetResource(menuKey, null, out object? menu) || menu is not ContextMenu contextMenu
                || !host.TryGetResource(ItemsKey, null, out object? items) || items is not ContextMenu source)
            {
                continue;
            }

            // The resource is a fresh menu per lookup that only carries the items; they leave it before joining the
            // Dock menu.
            object?[] controls = source.Items.ToArray();
            source.Items.Clear();
            foreach (object? control in controls)
            {
                contextMenu.Items.Add(control);
            }

            extended++;
        }

        return extended;
    }
}
