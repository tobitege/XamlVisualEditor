using System;
using System.Collections;
using System.Linq;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Xaml.Interactivity;
using XamlVisualEditor.Shell;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Appends the file commands of <c>DocumentFileMenuItems</c> and the extension items of the tab to the document context
/// menus of the Dock theme. The theme shares one menu per resource among all tabs, so the items are appended once at
/// startup.
/// </summary>
internal static class DocumentTabContextMenus
{
    internal const string ItemsKey = "DocumentFileMenuItems";

    internal const string ExtensionItemTemplateKey = "ExtensionMenuItemTemplate";

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

            AddExtensionItems(host, contextMenu);
            extended++;
        }

        return extended;
    }

    // The extension items follow the file commands. Every tab that opens the menu becomes its data context, so the
    // items and their arguments come from the dockable of that tab.
    private static void AddExtensionItems(IResourceHost host, ContextMenu contextMenu)
    {
        if (!host.TryGetResource(ExtensionItemTemplateKey, null, out object? template)
            || template is not IDataTemplate itemTemplate)
        {
            return;
        }

        IObservable<object?> dockable = contextMenu.GetObservable(StyledElement.DataContextProperty);
        contextMenu.ItemTemplate = itemTemplate;
        contextMenu.Bind(ExtensionMenu.CommandArgumentsProperty, dockable.Select(ExtensionMenu.GetTargetArguments));
        MenuItemsBehavior behavior = new() { InsertIndex = contextMenu.Items.Count };
        behavior.Bind(MenuItemsBehavior.ItemsProperty,
            dockable.Select(target => (IEnumerable?)(target as IFileDockable)?.TabCommands?.ExtensionItems));
        Interaction.GetBehaviors(contextMenu).Add(behavior);
    }
}
