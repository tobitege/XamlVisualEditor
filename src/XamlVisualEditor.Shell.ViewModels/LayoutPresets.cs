using System;
using System.Collections.Generic;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>
/// Ready-made docking layout shipped with the editor. A preset builds a new layout that contains only
/// the listed panels; every other panel opens from the view menu. Solution Explorer and File Explorer
/// are part of every preset.
/// </summary>
/// <param name="Id">Stable identifier used for commands and tests.</param>
/// <param name="Title">Localized menu title.</param>
/// <param name="VisiblePanelIds">Extension view ids (for example <c>toolbox.panel</c>) or built-in tool ids (for example <c>Breakpoints</c>).</param>
/// <param name="CollapseBottomDock">Collapses the bottom tool dock into its auto-hide strip.</param>
/// <param name="ExpandPinnedPanels">Unpins visible panels that register as auto-hide so they show as regular tabs.</param>
public sealed record LayoutPreset(
    string Id,
    string Title,
    IReadOnlySet<string> VisiblePanelIds,
    bool CollapseBottomDock,
    bool ExpandPinnedPanels = false)
{
    /// <summary>Returns true when the panel with the given id is part of this preset.</summary>
    public bool IsVisible(string? panelId)
    {
        return !string.IsNullOrWhiteSpace(panelId) && VisiblePanelIds.Contains(panelId);
    }
}

/// <summary>
/// Catalog of the layout presets shipped with the editor.
/// </summary>
public static class LayoutPresetCatalog
{
    public const string DesignerId = "designer";
    public const string CodeId = "code";
    public const string DebuggingId = "debugging";
    public const string AssistantsId = "assistants";
    public const string MinimalId = "minimal";

    /// <summary>The preset of the first start and of the reset layout command.</summary>
    public const string DefaultId = DebuggingId;

    /// <summary>Creates the presets with localized titles.</summary>
    public static IReadOnlyList<LayoutPreset> Create()
    {
        return new[]
        {
            new LayoutPreset(
                DesignerId,
                UiStrings.LayoutPresetDesigner,
                Set(
                    "solutionExplorer.panel",
                    "fileExplorer.panel",
                    "toolbox.panel",
                    "propertyEditor.panel",
                    "visualTree.panel",
                    "logicalTree.panel",
                    "animationEditor.panel",
                    "output.panel",
                    "problems.panel",
                    "Output"),
                CollapseBottomDock: true),
            new LayoutPreset(
                CodeId,
                UiStrings.LayoutPresetCode,
                Set(
                    "solutionExplorer.panel",
                    "fileExplorer.panel",
                    "git.panel",
                    "references.panel",
                    "problems.panel",
                    "output.panel",
                    "lspSettings.panel",
                    "Output"),
                CollapseBottomDock: false),
            new LayoutPreset(
                DebuggingId,
                UiStrings.LayoutPresetDebugging,
                Set(
                    "solutionExplorer.panel",
                    "fileExplorer.panel",
                    "output.panel",
                    "problems.panel",
                    "debugSettings.panel",
                    "Output",
                    "Breakpoints",
                    "CallStack",
                    "Locals",
                    "Watches"),
                CollapseBottomDock: false),
            new LayoutPreset(
                AssistantsId,
                UiStrings.LayoutPresetAssistants,
                Set(
                    "solutionExplorer.panel",
                    "fileExplorer.panel",
                    "acp.panel",
                    "mcp.panel",
                    "idebridge.panel",
                    "collaboration.panel",
                    "output.panel",
                    "problems.panel",
                    "Output"),
                CollapseBottomDock: true,
                ExpandPinnedPanels: true),
            new LayoutPreset(
                MinimalId,
                UiStrings.LayoutPresetMinimal,
                Set("solutionExplorer.panel", "fileExplorer.panel"),
                CollapseBottomDock: true)
        };
    }

    private static IReadOnlySet<string> Set(params string[] ids)
    {
        return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
    }
}
