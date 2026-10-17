using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Carries the command arguments of a context menu to its extension items: the items pass them as command parameter
/// and are hidden while the menu has none.
/// </summary>
public sealed class ExtensionMenu
{
    public static readonly AttachedProperty<IReadOnlyList<object?>?> CommandArgumentsProperty =
        AvaloniaProperty.RegisterAttached<ExtensionMenu, ContextMenu, IReadOnlyList<object?>?>("CommandArguments");

    /// <summary>Converts the explorer node or document tab a context menu acts on with <see cref="GetTargetArguments"/>.</summary>
    public static readonly IValueConverter TargetArguments =
        new FuncValueConverter<object?, IReadOnlyList<object?>?>(GetTargetArguments);

    public static IReadOnlyList<object?>? GetCommandArguments(ContextMenu menu) => menu.GetValue(CommandArgumentsProperty);

    public static void SetCommandArguments(ContextMenu menu, IReadOnlyList<object?>? value) =>
        menu.SetValue(CommandArgumentsProperty, value);

    /// <summary>
    /// Returns [full path] for an explorer node with a path or a document tab that shows a file, otherwise null.
    /// </summary>
    public static IReadOnlyList<object?>? GetTargetArguments(object? target)
    {
        string? path = target switch
        {
            ExtensionTreeNodeViewModel node => node.ExplorerPath,
            SolutionExplorerNodeViewModel node => node.ExplorerPath,
            IFileDockable { TabCommands: not null } dockable => dockable.FilePath,
            _ => null
        };
        return string.IsNullOrEmpty(path) ? null : [path];
    }
}
