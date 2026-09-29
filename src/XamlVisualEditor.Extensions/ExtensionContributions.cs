using System;
using System.Collections.Generic;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Extensions;

public enum ExtensionViewType
{
    Tree,
    Webview,
    Custom
}

public enum ExtensionViewLocation
{
    Left,
    Right,
    Bottom
}

/// <summary>A menu item of an extension. The title "-" is reserved: menus draw an item with that title as a separator.</summary>
public sealed record ExtensionMenuContribution(
    string CommandId,
    string Title,
    string? Location,
    string? Group,
    int Priority = 0)
{
    /// <summary>Gets the title of the submenu that holds the item; items with the same title and group share one submenu.</summary>
    public string? Submenu { get; init; }

    /// <summary>Gets the group of the item inside its submenu; separators divide the groups.</summary>
    public string? SubmenuGroup { get; init; }

    /// <summary>
    /// Gets the condition, in the syntax of <see cref="CommandMetadata.When"/>, under which a toggle item shows a check
    /// mark; <see langword="null"/> shows a plain item. The host evaluates it again whenever <see cref="IContextKeys"/> change.
    /// </summary>
    public string? CheckedWhen { get; init; }
}

public sealed record ExtensionToolbarContribution(
    string CommandId,
    string Title,
    string? Tooltip,
    string? Location,
    string? Group,
    int Priority = 0,
    string? IconPathData = null);

public sealed record ExtensionCommandPaletteContribution(string CommandId, string Title, string? Category);

public sealed record ExtensionPropertyEditorContribution(
    /// <summary>
    /// Lookup key for property editor contribution.
    /// This can be a CLR type name (preferred) or a well-known property name fallback.
    /// </summary>
    string PropertyType,
    PropertyEditorKind Kind,
    IReadOnlyList<string>? EnumOptions = null,
    IReadOnlyList<string>? BrushPresets = null);

public sealed record ExtensionViewContribution(
    string ViewId,
    string Title,
    ExtensionViewType Type,
    ExtensionViewLocation Location,
    int Priority,
    bool ActivateByDefault = false,
    string? ContainerId = null,
    bool PersistDockState = true)
{
    /// <summary>Places new panels in the collapsed side bar by default.</summary>
    public bool AutoHideByDefault { get; init; }
}

/// <summary>Known menu locations for extensions.</summary>
public static class ExtensionMenuLocations
{
    public const string File = "menu.file";
    public const string FileNew = "menu.file.new";
    public const string Edit = "menu.edit";
    public const string View = "menu.view";
    public const string ViewServers = "menu.view.servers";
    public const string Debug = "menu.debug";
    public const string Tools = "menu.tools";
    public const string ToolsWorkspace = "menu.tools.workspace";
    public const string Extensions = "menu.extensions";

    /// <summary>The context menu of the text and code editors.</summary>
    public const string EditorContext = "menu.editor.context";
}

/// <summary>Known toolbar locations for extensions.</summary>
public static class ExtensionToolbarLocations
{
    public const string Main = "toolbar.main";
    public const string Extensions = "toolbar.extensions";
}

public interface IExtensionContributionRegistry
{
    event EventHandler? Changed;

    IReadOnlyList<ExtensionMenuContribution> MenuItems { get; }

    IReadOnlyList<ExtensionToolbarContribution> ToolbarItems { get; }

    IReadOnlyList<ExtensionCommandPaletteContribution> CommandPaletteItems { get; }

    IReadOnlyList<ExtensionPropertyEditorContribution> PropertyEditors { get; }

    IReadOnlyList<ExtensionViewContribution> ViewContributions { get; }

    /// <summary>Gets theme providers to load in the isolated preview application.</summary>
    IReadOnlyList<DesignerThemeProviderRegistration> DesignerThemeProviders { get; }

    /// <summary>Provides theme providers for the editor interface.</summary>
    IReadOnlyList<EditorThemeProviderRegistration> EditorThemeProviders { get; }

    /// <summary>Registers editor themes independently of the preview themes.</summary>
    IDisposable RegisterEditorThemeProviders(string extensionId, IReadOnlyList<EditorThemeProviderRegistration> providers);

    /// <summary>Registers globally unique provider IDs without loading their assemblies in the editor.</summary>
    IDisposable RegisterDesignerThemeProviders(string extensionId, IReadOnlyList<DesignerThemeProviderRegistration> providers);

    IDisposable RegisterMenuItems(string extensionId, IReadOnlyList<ExtensionMenuContribution> items);

    IDisposable RegisterToolbarItems(string extensionId, IReadOnlyList<ExtensionToolbarContribution> items);

    IDisposable RegisterCommandPaletteItems(string extensionId, IReadOnlyList<ExtensionCommandPaletteContribution> items);

    IDisposable RegisterPropertyEditors(string extensionId, IReadOnlyList<ExtensionPropertyEditorContribution> editors);

    IDisposable RegisterViews(string extensionId, IReadOnlyList<ExtensionViewContribution> views);
}
