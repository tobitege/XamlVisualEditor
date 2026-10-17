using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.ReactiveUI;
using Dock.Model.ReactiveUI.Controls;
using Dock.Serializer.SystemTextJson;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.Shell;

/// <summary>
/// Dock tool for the solution explorer panel.
/// </summary>
public sealed partial class SolutionExplorerTool : Tool
{
    [IgnoreDataMember]
    [Reactive]
    public partial SolutionExplorerViewModel? SolutionExplorerViewModel { get; set; }

    public SolutionExplorerTool()
    {
        Id = "SolutionExplorer";
        Title = UiStrings.SolutionExplorer;
    }

    public SolutionExplorerTool(SolutionExplorerViewModel solutionExplorerViewModel)
    {
        SolutionExplorerViewModel = solutionExplorerViewModel;
        Id = "SolutionExplorer";
        Title = UiStrings.SolutionExplorer;
    }
}

/// <summary>
/// Dock tool for the output panel.
/// </summary>
public sealed partial class OutputTool : Tool
{
    [IgnoreDataMember]
    [Reactive]
    public partial OutputViewModel? OutputViewModel { get; set; }

    public OutputTool()
    {
        Id = "Output";
        Title = UiStrings.Output;
    }

    public OutputTool(OutputViewModel outputViewModel)
    {
        OutputViewModel = outputViewModel;
        Id = "Output";
        Title = UiStrings.Output;
    }
}

/// <summary>
/// Dock tool for terminal sessions.
/// </summary>
public sealed partial class TerminalTool : Tool
{
    [IgnoreDataMember]
    [Reactive]
    public partial TerminalViewModel? TerminalViewModel { get; set; }

    public TerminalTool()
    {
        Id = "Terminal";
        Title = UiStrings.TerminalGroup;
    }

    public TerminalTool(TerminalViewModel terminalViewModel)
    {
        TerminalViewModel = terminalViewModel;
        Id = "Terminal-" + terminalViewModel.Id.ToString("N");
        Title = terminalViewModel.Title;
    }
}

/// <summary>
/// Dock tool for the breakpoints panel.
/// </summary>
public sealed partial class BreakpointsTool : Tool
{
    [IgnoreDataMember]
    [Reactive]
    public partial BreakpointsViewModel? BreakpointsViewModel { get; set; }

    public BreakpointsTool()
    {
        Id = "Breakpoints";
        Title = UiStrings.Breakpoints;
    }

    public BreakpointsTool(BreakpointsViewModel breakpoints)
    {
        BreakpointsViewModel = breakpoints;
        Id = "Breakpoints";
        Title = UiStrings.Breakpoints;
    }
}

/// <summary>
/// Dock tool for the call stack panel.
/// </summary>
public sealed partial class CallStackTool : Tool
{
    [IgnoreDataMember]
    [Reactive]
    public partial CallStackViewModel? CallStackViewModel { get; set; }

    public CallStackTool()
    {
        Id = "CallStack";
        Title = UiStrings.CallStack;
    }

    public CallStackTool(CallStackViewModel callStack)
    {
        CallStackViewModel = callStack;
        Id = "CallStack";
        Title = UiStrings.CallStack;
    }
}

/// <summary>
/// Dock tool for the locals panel.
/// </summary>
public sealed partial class LocalsTool : Tool
{
    [IgnoreDataMember]
    [Reactive]
    public partial LocalsViewModel? LocalsViewModel { get; set; }

    public LocalsTool()
    {
        Id = "Locals";
        Title = UiStrings.Locals;
    }

    public LocalsTool(LocalsViewModel locals)
    {
        LocalsViewModel = locals;
        Id = "Locals";
        Title = UiStrings.Locals;
    }
}

/// <summary>
/// Dock tool for the watches panel.
/// </summary>
public sealed partial class WatchesTool : Tool
{
    [IgnoreDataMember]
    [Reactive]
    public partial WatchesViewModel? WatchesViewModel { get; set; }

    public WatchesTool()
    {
        Id = "Watches";
        Title = UiStrings.Watches;
    }

    public WatchesTool(WatchesViewModel watches)
    {
        WatchesViewModel = watches;
        Id = "Watches";
        Title = UiStrings.Watches;
    }
}

/// <summary>
/// Dock tool for extension-contributed views.
/// </summary>
public sealed partial class ExtensionTool : Tool
{
    public const string IdPrefix = "Extension:";

    [IgnoreDataMember]
    [Reactive]
    public partial ExtensionViewModel? ExtensionViewModel { get; set; }

    public string? ViewId { get; set; }

    public bool PersistDockState { get; set; } = true;

    public ExtensionTool()
    {
        Id = "Extension";
        Title = UiStrings.Extension;
    }

    public ExtensionTool(ExtensionViewModel viewModel)
    {
        ExtensionViewModel = viewModel;
        ViewId = viewModel.ViewId;
        PersistDockState = viewModel.PersistDockState;
        Id = BuildId(viewModel.ViewId);
        Title = viewModel.Title;
    }

    public static string BuildId(string viewId)
    {
        return IdPrefix + viewId;
    }
}

/// <summary>
/// A dock document that shows a file; the document tab shows its full path as tooltip and offers the file commands
/// in its context menu.
/// </summary>
public interface IFileDockable
{
    /// <summary>Gets the full path of the shown file.</summary>
    string FilePath { get; }

    /// <summary>Gets the commands of the tab context menu, or null when the tab offers none.</summary>
    DocumentTabCommands? TabCommands { get; }
}

/// <summary>
/// Dock document for a XAML designer document.
/// </summary>
public sealed partial class DesignerDocument : Document, IFileDockable
{
    [IgnoreDataMember]
    public DesignerDocumentViewModel DocumentViewModel { get; }

    /// <inheritdoc />
    [IgnoreDataMember]
    public string FilePath => DocumentViewModel.FilePath;

    /// <inheritdoc />
    [IgnoreDataMember]
    public DocumentTabCommands? TabCommands { get; set; }

    public DesignerDocument(DesignerDocumentViewModel documentViewModel)
    {
        DocumentViewModel = documentViewModel;
        Id = documentViewModel.FilePath;
        Title = documentViewModel.FileName;
        CanClose = true;
    }
}

/// <summary>
/// Dock document for a text file.
/// </summary>
public sealed partial class TextDocument : Document, IFileDockable
{
    [IgnoreDataMember]
    public TextDocumentViewModel DocumentViewModel { get; }

    /// <inheritdoc />
    [IgnoreDataMember]
    public string FilePath => DocumentViewModel.FilePath;

    /// <inheritdoc />
    [IgnoreDataMember]
    public DocumentTabCommands? TabCommands { get; set; }

    public TextDocument(TextDocumentViewModel documentViewModel)
    {
        DocumentViewModel = documentViewModel;
        Id = documentViewModel.FilePath;
        Title = documentViewModel.FileName;
        CanClose = true;
    }
}

/// <summary>
/// Dock document for the infinite editor canvas.
/// </summary>
public sealed partial class InfiniteCanvasDocument : Document
{
    [IgnoreDataMember]
    [Reactive]
    public partial InfiniteCanvasViewModel? CanvasViewModel { get; set; }

    public InfiniteCanvasDocument()
    {
        Id = "InfiniteCanvas";
        Title = UiStrings.Canvas;
        CanClose = true;
    }

    public InfiniteCanvasDocument(InfiniteCanvasViewModel canvasViewModel)
    {
        CanvasViewModel = canvasViewModel;
        Id = "InfiniteCanvas";
        Title = UiStrings.Canvas;
        CanClose = true;
    }
}

/// <summary>
/// Dock document that shows the view of an extension view model. It belongs to the session: the layout never saves it,
/// and closing it disposes the view model.
/// </summary>
public sealed partial class ExtensionDocument : Document
{
    /// <summary>The prefix of the dock id before the document id of the extension.</summary>
    public const string ID_PREFIX = "ExtensionDocument:";

    public ExtensionDocument(string documentId, string title, string? toolTip, object viewModel)
    {
        Id = ID_PREFIX + documentId;
        Title = title;
        ToolTip = toolTip;
        ViewModel = viewModel;
        CanClose = true;
    }

    /// <summary>Gets the tooltip of the document tab, or null to show the title.</summary>
    [IgnoreDataMember]
    public string? ToolTip { get; }

    /// <summary>Gets the view model whose view the document shows.</summary>
    [IgnoreDataMember]
    public object ViewModel { get; }
}

/// <summary>
/// Factory that creates the default VS/Blend-style docking layout.
/// </summary>
public sealed partial class XamlEditorDockFactory : Factory
{
    public override IDocumentDock CreateDocumentDock() => new DocumentDock
    {
        EmptyContent = UiStrings.NoDocumentsOpen
    };

    private static readonly bool LogLayoutWarnings = true;
    private const string SolutionExplorerViewId = "solutionExplorer.panel";
    private const string LAYOUT_NAME_KEY = "LayoutName";
    private readonly MainWindowViewModel _mainVm;
    private readonly ILogger<XamlEditorDockFactory> _logger;
    private static readonly DockSerializer s_serializer = new(typeof(ObservableCollection<>));

    public XamlEditorDockFactory(
        MainWindowViewModel mainVm,
        ILogger<XamlEditorDockFactory>? logger = null)
    {
        _mainVm = mainVm;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<XamlEditorDockFactory>.Instance;
    }

    /// <summary>
    /// Creates the dock structure of a new layout with docking panels arranged in a VS/Blend style.
    /// </summary>
    /// <param name="includeTool">
    /// Selects the built-in tools (Output, Breakpoints, CallStack, Locals, Watches) of the bottom dock by id;
    /// null includes all of them. Extension panels are not part of the structure.
    /// </param>
    public IRootDock CreateDefaultLayout(Predicate<string>? includeTool = null)
    {
        // Left tools: extensions will populate

        // Bottom tools: output and debugging panels (extensions contribute the rest).
        IDockable[] bottomTools = new Tool[]
            {
                new OutputTool(_mainVm.Output),
                new BreakpointsTool(_mainVm.Breakpoints),
                new CallStackTool(_mainVm.CallStack),
                new LocalsTool(_mainVm.Locals),
                new WatchesTool(_mainVm.Watches)
            }
            .Where(tool => includeTool?.Invoke(tool.Id) ?? true)
            .ToArray();

        // Left tool dock
        ToolDock leftToolDock = new()
        {
            Id = "LeftToolDock",
            Title = UiStrings.LeftTools,
            Proportion = 0.2,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>(),
            Alignment = Alignment.Left
        };

        // Right tool dock
        ToolDock rightToolDock = new()
        {
            Id = "RightToolDock",
            Title = UiStrings.RightTools,
            Proportion = 0.25,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>(),
            Alignment = Alignment.Right
        };

        // Bottom tool dock
        ToolDock bottomToolDock = new()
        {
            Id = "BottomToolDock",
            Title = UiStrings.BottomTools,
            Proportion = 0.25,
            ActiveDockable = null,
            VisibleDockables = CreateList(bottomTools),
            Alignment = Alignment.Bottom,
            AutoHide = true,
            IsExpanded = false
        };

        // Document dock (center)
        DocumentDock documentDock = new()
        {
            Id = "DocumentDock",
            Title = UiStrings.Documents,
            IsCollapsable = false,
            VisibleDockables = CreateList<IDockable>(),
            ActiveDockable = null,
            CanCreateDocument = false,
            EnableWindowDrag = true
        };

        // Main layout: Left | (Center / Bottom) | Right
        ProportionalDock centerAndBottom = new()
        {
            Id = "CenterAndBottom",
            Orientation = Orientation.Vertical,
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>(
                documentDock,
                new ProportionalDockSplitter(),
                bottomToolDock)
        };

        ProportionalDock mainLayout = new()
        {
            Id = "MainLayout",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(
                leftToolDock,
                new ProportionalDockSplitter(),
                centerAndBottom,
                new ProportionalDockSplitter(),
                rightToolDock)
        };

        RootDock rootDock = new()
        {
            Id = "Root",
            Title = UiStrings.RootArea,
            IsCollapsable = false,
            ActiveDockable = mainLayout,
            DefaultDockable = mainLayout,
            VisibleDockables = CreateList<IDockable>(mainLayout)
        };

        EnsureLayoutDefaults(rootDock);

        return rootDock;
    }

    /// <summary>
    /// Ensures deserialized tools have their view model references wired.
    /// </summary>
    public void ConfigureToolViewModels(IRootDock rootDock)
    {
        SolutionExplorerTool? solutionTool = FindDockable<SolutionExplorerTool>(rootDock, "SolutionExplorer");
        if (solutionTool is not null)
        {
            solutionTool.SolutionExplorerViewModel = _mainVm.SolutionExplorer;
            solutionTool.Title = UiStrings.SolutionExplorer;
        }

        OutputTool? outputTool = FindDockable<OutputTool>(rootDock, "Output");
        if (outputTool is not null)
        {
            outputTool.OutputViewModel = _mainVm.Output;
            outputTool.Title = UiStrings.Output;
        }

        BreakpointsTool? breakpointsTool = FindDockable<BreakpointsTool>(rootDock, "Breakpoints");
        if (breakpointsTool is not null)
        {
            breakpointsTool.BreakpointsViewModel = _mainVm.Breakpoints;
            breakpointsTool.Title = UiStrings.Breakpoints;
        }

        CallStackTool? callStackTool = FindDockable<CallStackTool>(rootDock, "CallStack");
        if (callStackTool is not null)
        {
            callStackTool.CallStackViewModel = _mainVm.CallStack;
            callStackTool.Title = UiStrings.CallStack;
        }

        LocalsTool? localsTool = FindDockable<LocalsTool>(rootDock, "Locals");
        if (localsTool is not null)
        {
            localsTool.LocalsViewModel = _mainVm.Locals;
            localsTool.Title = UiStrings.Locals;
        }

        WatchesTool? watchesTool = FindDockable<WatchesTool>(rootDock, "Watches");
        if (watchesTool is not null)
        {
            watchesTool.WatchesViewModel = _mainVm.Watches;
            watchesTool.Title = UiStrings.Watches;
        }

        foreach (ExtensionTool extensionTool in FindDockables<ExtensionTool>(rootDock))
        {
            if (!string.IsNullOrWhiteSpace(extensionTool.ViewId)
                && _mainVm.TryGetExtensionView(extensionTool.ViewId, out ExtensionViewModel? viewModel)
                && viewModel is not null)
            {
                extensionTool.ExtensionViewModel = viewModel;
                extensionTool.Title = viewModel.Title;
            }
        }

        PruneTerminalTools(rootDock);
    }

    /// <summary>
    /// Ensures deserialized documents have their view model references wired.
    /// </summary>
    public void ConfigureDocumentViewModels(IRootDock rootDock)
    {
        InfiniteCanvasDocument? canvasDoc = FindDockable<InfiniteCanvasDocument>(rootDock, "InfiniteCanvas");
        if (canvasDoc is not null)
        {
            canvasDoc.CanvasViewModel = _mainVm.InfiniteCanvas;
        }
    }

    public override void InitLayout(IDockable layout)
    {
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        };

        base.InitLayout(layout);
    }

    public override void InitDockable(IDockable dockable, IDockable? owner)
    {
        DockCapabilityDefaults.Apply(dockable);
        base.InitDockable(dockable, owner);
    }

    public void EnsureOwnerReferences(IRootDock rootDock)
    {
        SetOwnerRecursive(rootDock, null);
    }

    /// <summary>
    /// Adds a document to the document dock.
    /// </summary>
    public DesignerDocument? AddDocument(IRootDock rootDock, DesignerDocumentViewModel documentVm)
    {
        DesignerDocument doc = new(documentVm);

        // Find the document dock
        DocumentDock? docDock = FindDockable<DocumentDock>(rootDock, "DocumentDock");
        if (docDock is not null)
        {
            AddDockable(docDock, doc);
            SetOwnerRecursive(doc, docDock);
            SetActiveDockable(doc);
            SetFocusedDockable(docDock, doc);
            return doc;
        }

        return null;
    }

    public TextDocument? AddTextDocument(IRootDock rootDock, TextDocumentViewModel documentVm)
    {
        TextDocument doc = new(documentVm);

        DocumentDock? docDock = FindDockable<DocumentDock>(rootDock, "DocumentDock");
        if (docDock is not null)
        {
            AddDockable(docDock, doc);
            SetOwnerRecursive(doc, docDock);
            SetActiveDockable(doc);
            SetFocusedDockable(docDock, doc);
            return doc;
        }

        return null;
    }

    public InfiniteCanvasDocument? AddCanvasDocument(IRootDock rootDock, InfiniteCanvasViewModel canvasViewModel)
    {
        return AddDocument(rootDock, new InfiniteCanvasDocument(canvasViewModel));
    }

    /// <summary>Adds a document to the document dock and activates it; null when the layout has no document dock.</summary>
    public T? AddDocument<T>(IRootDock rootDock, T doc)
        where T : Document
    {
        DocumentDock? docDock = FindDockable<DocumentDock>(rootDock, "DocumentDock");
        if (docDock is not null)
        {
            AddDockable(docDock, doc);
            SetOwnerRecursive(doc, docDock);
            SetActiveDockable(doc);
            SetFocusedDockable(docDock, doc);
            return doc;
        }

        return null;
    }

    public TerminalTool? AddTerminalTool(IRootDock rootDock, TerminalViewModel terminalViewModel)
    {
        ToolDock? toolDock = FindDockable<ToolDock>(rootDock, "BottomToolDock");
        if (toolDock is not null)
        {
            bool wasCollapsed = toolDock.Proportion == 0;
            TerminalTool tool = new(terminalViewModel)
            {
                Title = terminalViewModel.Title
            };
            AddDockable(toolDock, tool);
            SetOwnerRecursive(tool, toolDock);
            toolDock.IsEmpty = false;
            if (wasCollapsed)
            {
                PinDockable(tool);
                EnsurePinnedToolSize(tool);
                PreviewPinnedDockable(tool);
            }
            else
            {
                SetActiveDockable(tool);
                SetFocusedDockable(toolDock, tool);
            }
            return tool;
        }

        return null;
    }

    /// <summary>
    /// Collapses the bottom tool dock without changing its tools or saved proportion.
    /// </summary>
    public void CollapseBottomToolDock(IRootDock rootDock)
    {
        IReadOnlyList<ToolDock> bottomDocks = FindDockables<ToolDock>(rootDock)
            .Where(dock => string.Equals(dock.Id, "BottomToolDock", StringComparison.Ordinal))
            .ToList();
        foreach (ToolDock bottomDock in bottomDocks)
        {
            List<IDockable> tools = bottomDock.VisibleDockables?.ToList()
                ?? new List<IDockable>();
            foreach (IDockable tool in tools)
            {
                PinDockable(tool);
                EnsurePinnedToolSize(tool);
            }

            if (double.IsFinite(bottomDock.Proportion) && bottomDock.Proportion > 0)
            {
                bottomDock.CollapsedProportion = bottomDock.Proportion;
            }

            bottomDock.Proportion = 0;
            bottomDock.ActiveDockable = null;
            bottomDock.IsActive = false;
            bottomDock.IsExpanded = false;
        }

        if (rootDock.PinnedDock is ToolDock pinnedDock)
        {
            pinnedDock.ActiveDockable = null;
            pinnedDock.IsActive = false;
            pinnedDock.IsExpanded = false;
        }
    }

    private static void EnsurePinnedToolSize(IDockable tool)
    {
        tool.GetPinnedBounds(out _, out _, out double width, out double height);
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
            tool.SetPinnedBounds(0, 0, 400, 300);
    }

    public ExtensionTool? AddExtensionTool(IRootDock rootDock, ExtensionViewModel viewModel)
    {
        string fallbackDockId = viewModel.Location switch
        {
            ExtensionViewLocation.Left => "LeftToolDock",
            ExtensionViewLocation.Right => "RightToolDock",
            ExtensionViewLocation.Bottom => "BottomToolDock",
            _ => "RightToolDock"
        };

        ToolDock? toolDock = null;
        if (!string.IsNullOrWhiteSpace(viewModel.ContainerId))
        {
            toolDock = FindDockable<ToolDock>(rootDock, viewModel.ContainerId);
            if (toolDock is null)
            {
                _logger.LogDebug(
                    "Extension container '{ContainerId}' was not found for view '{ViewId}'. Falling back to location dock.",
                    viewModel.ContainerId,
                    viewModel.ViewId);
            }
        }

        toolDock ??= FindDockable<ToolDock>(rootDock, fallbackDockId);
        if (toolDock is not null)
        {
            bool isBottomToolDock = string.Equals(toolDock.Id, "BottomToolDock", StringComparison.Ordinal);
            bool wasBottomCollapsed = isBottomToolDock && toolDock.Proportion == 0;
            double collapsedProportion = toolDock.CollapsedProportion;
            ExtensionTool tool = new(viewModel);
            int? desiredInsertIndex = null;
            if (toolDock.VisibleDockables is ObservableCollection<IDockable> dockables)
            {
                int insertIndex = dockables.Count;
                if (viewModel.Location == ExtensionViewLocation.Left)
                {
                    int solutionIndex = -1;
                    for (int i = 0; i < dockables.Count; i++)
                    {
                        if (dockables[i] is SolutionExplorerTool)
                        {
                            solutionIndex = i;
                            break;
                        }

                        if (dockables[i] is ExtensionTool extensionTool
                            && string.Equals(extensionTool.ViewId, SolutionExplorerViewId, StringComparison.OrdinalIgnoreCase))
                        {
                            solutionIndex = i;
                            break;
                        }
                    }

                    if (solutionIndex >= 0 && solutionIndex <= dockables.Count)
                    {
                        insertIndex = Math.Min(solutionIndex + 1, dockables.Count);
                    }
                    else
                    {
                        insertIndex = 0;
                    }
                }

                desiredInsertIndex = insertIndex;
            }

            AddDockable(toolDock, tool);
            SetOwnerRecursive(tool, toolDock);
            toolDock.IsEmpty = false;

            if (desiredInsertIndex is int targetIndex
                && toolDock.VisibleDockables is ObservableCollection<IDockable> orderedDockables)
            {
                int currentIndex = orderedDockables.IndexOf(tool);
                int boundedTargetIndex = Math.Clamp(targetIndex, 0, orderedDockables.Count - 1);
                if (currentIndex >= 0 && currentIndex != boundedTargetIndex)
                {
                    orderedDockables.Move(currentIndex, boundedTargetIndex);
                }
            }

            if (viewModel.AutoHideByDefault)
            {
                PinDockable(tool);
                EnsurePinnedToolSize(tool);
            }
            else if (wasBottomCollapsed)
            {
                CollapseBottomToolDock(rootDock);
                if (double.IsFinite(collapsedProportion) && collapsedProportion > 0)
                    toolDock.CollapsedProportion = collapsedProportion;
            }
            else if (viewModel.ActivateByDefault || (!isBottomToolDock && toolDock.ActiveDockable is null))
            {
                SetActiveDockable(tool);
                SetFocusedDockable(toolDock, tool);
            }
            return tool;
        }

        return null;
    }

    public static T? FindDockable<T>(IDockable dockable, string id) where T : class, IDockable
    {
        return FindDockables<T>(dockable).FirstOrDefault(candidate => candidate.Id == id);
    }

    public static IReadOnlyList<T> FindDockables<T>(IDockable dockable) where T : class, IDockable
    {
        List<T> results = new();
        CollectDockables(dockable, results, new HashSet<IDockable>(ReferenceEqualityComparer.Instance));
        return results;
    }

    private static void CollectDockables<T>(IDockable dockable, List<T> results, HashSet<IDockable> visited)
        where T : class, IDockable
    {
        if (!visited.Add(dockable)) return;
        if (dockable is T typed)
        {
            results.Add(typed);
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (IDockable child in dock.VisibleDockables)
            {
                CollectDockables(child, results, visited);
            }
        }
        if (dockable is IRootDock root)
        {
            foreach (IList<IDockable>? children in new[]
                     {
                         root.HiddenDockables, root.LeftPinnedDockables, root.RightPinnedDockables,
                         root.TopPinnedDockables, root.BottomPinnedDockables
                     })
            {
                if (children is null) continue;
                foreach (IDockable child in children)
                    CollectDockables(child, results, visited);
            }
            if (root.PinnedDock is not null)
                CollectDockables(root.PinnedDock, results, visited);

            // Floating windows hold their tools in a layout of their own.
            if (root.Windows is not null)
            {
                foreach (IDockWindow window in root.Windows)
                {
                    if (window.Layout is not null)
                        CollectDockables(window.Layout, results, visited);
                }
            }
        }
    }

    /// <summary>
    /// Gets the default layout file path.
    /// </summary>
    public static string GetDefaultLayoutPath()
    {
        return Path.Combine(ViewModels.ShellSettingsPaths.GetRoot(), "dock-layout.json");
    }

    /// <summary>
    /// Saves the current dock layout to a JSON file.
    /// </summary>
    /// <param name="rootDock">The layout to save.</param>
    /// <param name="filePath">The target file; null saves the working layout of the next start.</param>
    /// <param name="layoutName">The name of the saved user layout that the working layout came from, if any.</param>
    /// <returns>False when the layout could not be written; the previous file then stays unchanged.</returns>
    public bool SaveLayout(IRootDock rootDock, string? filePath = null, string? layoutName = null)
    {
        if (filePath is null && !HasDesktopLifetime()) return true;
        filePath ??= GetDefaultLayoutPath();
        string tempPath = filePath + ".tmp";
        List<Action> restoreActions = DetachRuntimeDocuments(rootDock);
        restoreActions.InsertRange(0, DetachNonPersistentExtensionTools(rootDock));
        restoreActions.InsertRange(0, DetachToolViewModels(rootDock));
        restoreActions.InsertRange(0, DetachDockRuntimeReferences(rootDock));
        try
        {
            JsonObject snapshot = JsonNode.Parse(s_serializer.Serialize(rootDock)) as JsonObject
                ?? throw new InvalidOperationException("The dock serializer did not return a layout object.");
            snapshot.Remove(nameof(IRootDock.PinnedDock));
            snapshot.Remove(nameof(IRootDock.Window));
            snapshot.Remove(nameof(IRootDock.Windows));
            JsonObject pinnedSizes = new();
            foreach (IDockable tool in rootDock.BottomPinnedDockables ?? Array.Empty<IDockable>())
            {
                tool.GetPinnedBounds(out _, out _, out double width, out double height);
                if (!string.IsNullOrWhiteSpace(tool.Id) && double.IsFinite(width) && width > 0
                    && double.IsFinite(height) && height > 0)
                    pinnedSizes[tool.Id] = new JsonObject { ["Width"] = width, ["Height"] = height };
            }
            snapshot["PinnedToolSizes"] = pinnedSizes;
            if (!string.IsNullOrWhiteSpace(layoutName))
            {
                snapshot[LAYOUT_NAME_KEY] = layoutName;
            }

            // The move replaces the file in one step, so an interrupted write never leaves a truncated layout.
            File.WriteAllText(tempPath, snapshot.ToJsonString());
            File.Move(tempPath, filePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            if (LogLayoutWarnings)
            {
                _logger.LogWarning("Failed to save dock layout: {Message}", ex.Message);
            }

            TryDeleteFile(tempPath);
            return false;
        }
        finally
        {
            foreach (Action restore in restoreActions)
            {
                restore();
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Loads a dock layout from a JSON file.
    /// </summary>
    public IRootDock? LoadLayout(string? filePath = null) => LoadLayout(out _, filePath);

    /// <summary>
    /// Loads a dock layout from a JSON file together with the name of the user layout it came from.
    /// </summary>
    public IRootDock? LoadLayout(out string? layoutName, string? filePath = null)
    {
        layoutName = null;
        if (filePath is null && !HasDesktopLifetime()) return null;
        filePath ??= GetDefaultLayoutPath();
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            string json = SharedFileReader.ReadAllText(filePath);
            JsonObject? persisted = JsonNode.Parse(json) as JsonObject;
            JsonObject? pinnedSizes = persisted?["PinnedToolSizes"] as JsonObject;
            persisted?.Remove("PinnedToolSizes");
            if (persisted?[LAYOUT_NAME_KEY] is JsonValue nameValue && nameValue.TryGetValue(out string? storedName))
            {
                layoutName = storedName;
            }

            persisted?.Remove(LAYOUT_NAME_KEY);
            persisted?.Remove(nameof(IRootDock.PinnedDock));
            persisted?.Remove(nameof(IRootDock.Window));
            persisted?.Remove(nameof(IRootDock.Windows));
            RemoveExtensionManagerDock(persisted);
            RemoveRuntimeDocuments(persisted);
            if (persisted is not null && persisted[nameof(IRootDock.ActiveDockable)] is null)
            {
                persisted.Remove(nameof(IRootDock.DefaultDockable));
            }
            IRootDock? rootDock = s_serializer.Deserialize<IRootDock>(persisted?.ToJsonString() ?? json);
            if (rootDock is not null)
            {
                EnsureLayoutDefaults(rootDock);
                NormalizeDockReferences(rootDock);
                foreach (IDockable tool in rootDock.BottomPinnedDockables ?? Array.Empty<IDockable>())
                {
                    if (!string.IsNullOrWhiteSpace(tool.Id) && pinnedSizes?[tool.Id] is JsonObject size
                        && size["Width"] is JsonValue widthValue && widthValue.TryGetValue(out double width)
                        && size["Height"] is JsonValue heightValue && heightValue.TryGetValue(out double height)
                        && double.IsFinite(width) && width > 0 && double.IsFinite(height) && height > 0)
                        tool.SetPinnedBounds(0, 0, width, height);
                    EnsurePinnedToolSize(tool);
                }
                IDockable? mainLayout = rootDock.VisibleDockables?
                    .FirstOrDefault(dockable => dockable.Id == "MainLayout");
                rootDock.ActiveDockable ??= mainLayout;
                rootDock.DefaultDockable ??= mainLayout;
                bool hasLeft = FindDockable<ToolDock>(rootDock, "LeftToolDock") is not null;
                bool hasRight = FindDockable<ToolDock>(rootDock, "RightToolDock") is not null;
                bool hasBottom = FindDockable<ToolDock>(rootDock, "BottomToolDock") is not null;
                bool hasDocuments = FindDockable<DocumentDock>(rootDock, "DocumentDock") is not null;
                if (!hasLeft || !hasRight || !hasBottom || !hasDocuments)
                {
                    if (LogLayoutWarnings)
                    {
                        _logger.LogWarning("Dock layout missing required docks. Resetting to defaults.");
                    }
                    return null;
                }
            }
            return rootDock;
        }
        catch (Exception ex)
        {
            if (LogLayoutWarnings)
            {
                _logger.LogWarning("Failed to load dock layout: {Message}", ex.Message);
            }
            return null;
        }
    }

    private static void RemoveExtensionManagerDock(JsonNode? node) => RemoveMatchingNodes(node, IsExtensionManagerDock);

    // Open documents belong to the session, not to the layout, and cannot be deserialized. SaveLayout leaves them
    // out; a layout file that an earlier version wrote with a document reference must still load.
    private static void RemoveRuntimeDocuments(JsonNode? node) => RemoveMatchingNodes(node, IsRuntimeDocument);

    private static void RemoveMatchingNodes(JsonNode? node, Func<JsonNode?, bool> matches)
    {
        if (node is JsonArray items)
        {
            for (int index = items.Count - 1; index >= 0; index--)
            {
                if (matches(items[index])) items.RemoveAt(index);
                else RemoveMatchingNodes(items[index], matches);
            }
        }
        else if (node is JsonObject entry)
        {
            foreach (KeyValuePair<string, JsonNode?> property in entry.ToArray())
            {
                if (matches(property.Value)) entry.Remove(property.Key);
                else RemoveMatchingNodes(property.Value, matches);
            }
        }
    }

    private static bool IsExtensionManagerDock(JsonNode? node) =>
        node is JsonObject entry && entry[nameof(IDockable.Id)]?.GetValue<string>() == "ExtensionsManager";

    private static bool IsRuntimeDocument(JsonNode? node) =>
        node is JsonObject entry
        && entry["$type"]?.GetValue<string>() is { } type
        && (type == typeof(TextDocument).FullName || type == typeof(DesignerDocument).FullName
            || type == typeof(ExtensionDocument).FullName);

    /// <summary>
    /// Gives a side tool dock without width its last width, or the default width, when it shows a tool again.
    /// A side dock keeps a zero proportion after its last tool was hidden, which would leave the tool invisible.
    /// </summary>
    public static void RestoreSideDockWidth(IDockable dockable)
    {
        if (dockable.Owner is not ToolDock dock) return;
        double defaultWidth = dock.Id switch
        {
            "LeftToolDock" => DefaultLeftToolDockWidth,
            "RightToolDock" => DefaultRightToolDockWidth,
            _ => 0
        };
        if (defaultWidth <= 0 || (double.IsFinite(dock.Proportion) && dock.Proportion > 0)) return;

        dock.Proportion = double.IsFinite(dock.CollapsedProportion) && dock.CollapsedProportion > 0
            ? dock.CollapsedProportion
            : defaultWidth;
        dock.IsEmpty = false;
    }

    private const double DefaultLeftToolDockWidth = 0.2;
    private const double DefaultRightToolDockWidth = 0.25;

    public static void EnsureLayoutDefaults(IRootDock rootDock)
    {
        const double leftWidth = DefaultLeftToolDockWidth;
        const double rightWidth = DefaultRightToolDockWidth;
        const double bottomHeight = 0.25;
        const double documentWidth = 0.75;

        if (string.IsNullOrWhiteSpace(rootDock.Id))
        {
            rootDock.Id = "Root";
        }

        rootDock.Title = UiStrings.RootArea;
        foreach (IDockable dock in FindDockables<IDockable>(rootDock))
        {
            if (dock is IDocumentDock emptyDocumentDock)
            {
                emptyDocumentDock.EmptyContent = UiStrings.NoDocumentsOpen;
            }

            string? title = dock.Id switch
            {
                "LeftToolDock" => UiStrings.LeftTools,
                "RightToolDock" => UiStrings.RightTools,
                "BottomToolDock" => UiStrings.BottomTools,
                "DocumentDock" => UiStrings.Documents,
                "PinnedDock" => UiStrings.Pinned,
                _ => null
            };
            if (title is not null) dock.Title = title;
        }

        rootDock.VisibleDockables ??= new ObservableCollection<IDockable>();
        rootDock.HiddenDockables ??= new ObservableCollection<IDockable>();
        rootDock.LeftPinnedDockables ??= new ObservableCollection<IDockable>();
        rootDock.RightPinnedDockables ??= new ObservableCollection<IDockable>();
        rootDock.TopPinnedDockables ??= new ObservableCollection<IDockable>();
        rootDock.BottomPinnedDockables ??= new ObservableCollection<IDockable>();
        rootDock.Windows ??= new ObservableCollection<IDockWindow>();

        if (rootDock.PinnedDock is null)
        {
            rootDock.PinnedDock = new ToolDock
            {
                Id = "PinnedDock",
                Title = UiStrings.Pinned,
                Alignment = Alignment.Left,
                VisibleDockables = new ObservableCollection<IDockable>()
            };
        }

        rootDock.DockGroup ??= "Root";
        rootDock.CanDrag = true;
        rootDock.CanDrop = true;

        ToolDock? bottomDock = FindDockable<ToolDock>(rootDock, "BottomToolDock");
        if (bottomDock is not null)
        {
            bottomDock.VisibleDockables ??= new ObservableCollection<IDockable>();
            if (!double.IsFinite(bottomDock.Proportion) || bottomDock.Proportion < 0)
            {
                bottomDock.Proportion = bottomHeight;
            }
            if (bottomDock.VisibleDockables.Count > 0)
            {
                bottomDock.IsEmpty = false;
            }
        }

        ToolDock? leftDock = FindDockable<ToolDock>(rootDock, "LeftToolDock");
        if (leftDock is not null)
        {
            leftDock.VisibleDockables ??= new ObservableCollection<IDockable>();
            if (double.IsNaN(leftDock.Proportion) || leftDock.Proportion <= 0)
            {
                leftDock.Proportion = leftWidth;
            }
            if (leftDock.VisibleDockables.Count > 0)
            {
                leftDock.IsEmpty = false;
            }
        }

        ToolDock? rightDock = FindDockable<ToolDock>(rootDock, "RightToolDock");
        if (rightDock is not null)
        {
            rightDock.VisibleDockables ??= new ObservableCollection<IDockable>();
            if (double.IsNaN(rightDock.Proportion) || rightDock.Proportion <= 0)
            {
                rightDock.Proportion = rightWidth;
            }
            if (rightDock.VisibleDockables.Count > 0)
            {
                rightDock.IsEmpty = false;
            }
        }

        DocumentDock? documentDock = FindDockable<DocumentDock>(rootDock, "DocumentDock");
        if (documentDock is not null && (double.IsNaN(documentDock.Proportion) || documentDock.Proportion <= 0))
        {
            documentDock.Proportion = documentWidth;
        }
    }

    private static List<Action> DetachToolViewModels(IRootDock rootDock)
    {
        List<Action> restore = new();

        DetachToolViewModel(
            FindDockable<SolutionExplorerTool>(rootDock, "SolutionExplorer"),
            tool => tool.SolutionExplorerViewModel,
            (tool, vm) => tool.SolutionExplorerViewModel = vm,
            restore);

        DetachToolViewModel(
            FindDockable<OutputTool>(rootDock, "Output"),
            tool => tool.OutputViewModel,
            (tool, vm) => tool.OutputViewModel = vm,
            restore);

        DetachToolViewModel(
            FindDockable<BreakpointsTool>(rootDock, "Breakpoints"),
            tool => tool.BreakpointsViewModel,
            (tool, vm) => tool.BreakpointsViewModel = vm,
            restore);

        DetachToolViewModel(
            FindDockable<CallStackTool>(rootDock, "CallStack"),
            tool => tool.CallStackViewModel,
            (tool, vm) => tool.CallStackViewModel = vm,
            restore);

        DetachToolViewModel(
            FindDockable<LocalsTool>(rootDock, "Locals"),
            tool => tool.LocalsViewModel,
            (tool, vm) => tool.LocalsViewModel = vm,
            restore);

        DetachToolViewModel(
            FindDockable<WatchesTool>(rootDock, "Watches"),
            tool => tool.WatchesViewModel,
            (tool, vm) => tool.WatchesViewModel = vm,
            restore);

        foreach (TerminalTool terminalTool in FindDockables<TerminalTool>(rootDock))
        {
            DetachToolViewModel(
                terminalTool,
                tool => tool.TerminalViewModel,
                (tool, vm) => tool.TerminalViewModel = vm,
                restore);
        }

        foreach (ExtensionTool extensionTool in FindDockables<ExtensionTool>(rootDock))
        {
            DetachToolViewModel(
                extensionTool,
                tool => tool.ExtensionViewModel,
                (tool, vm) => tool.ExtensionViewModel = vm,
                restore);
        }

        return restore;
    }

    private static IReadOnlyList<Action> DetachNonPersistentExtensionTools(IRootDock rootDock)
    {
        List<Action> restore = new();
        foreach (ExtensionTool extensionTool in FindDockables<ExtensionTool>(rootDock))
        {
            if (extensionTool.PersistDockState || extensionTool.Owner is not IDock owner || owner.VisibleDockables is null)
            {
                continue;
            }

            int index = owner.VisibleDockables.IndexOf(extensionTool);
            if (index < 0)
            {
                continue;
            }

            bool wasActive = ReferenceEquals(owner.ActiveDockable, extensionTool);
            owner.VisibleDockables.RemoveAt(index);
            if (wasActive)
            {
                owner.ActiveDockable = owner.VisibleDockables.FirstOrDefault();
            }

            restore.Add(() =>
            {
                if (owner.VisibleDockables is null || owner.VisibleDockables.Contains(extensionTool))
                {
                    return;
                }

                int insertIndex = Math.Clamp(index, 0, owner.VisibleDockables.Count);
                owner.VisibleDockables.Insert(insertIndex, extensionTool);
                if (wasActive)
                {
                    owner.ActiveDockable = extensionTool;
                }
            });
        }

        return restore;
    }

    private static void PruneTerminalTools(IRootDock rootDock)
    {
        IReadOnlyList<TerminalTool> terminalTools = FindDockables<TerminalTool>(rootDock);
        foreach (TerminalTool tool in terminalTools)
        {
            if (tool.TerminalViewModel is null && tool.Owner is IDock dock && dock.VisibleDockables is not null)
            {
                dock.VisibleDockables.Remove(tool);
            }
        }
    }

    private static void DetachToolViewModel<TTool, TViewModel>(
        TTool? tool,
        Func<TTool, TViewModel?> getter,
        Action<TTool, TViewModel?> setter,
        List<Action> restore)
        where TTool : class
        where TViewModel : class
    {
        if (tool is null)
        {
            return;
        }

        TViewModel? current = getter(tool);
        if (current is null)
        {
            return;
        }

        setter(tool, null);
        restore.Add(() => setter(tool, current));
    }

    private static void NormalizeDockReferences(IRootDock rootDock)
    {
        IReadOnlyList<IDockable> dockables = FindDockables<IDockable>(rootDock);
        Dictionary<string, IDockable> byId = dockables.Where(item => !string.IsNullOrEmpty(item.Id))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (IDock dock in dockables.OfType<IDock>())
        {
            dock.ActiveDockable = Resolve(dock.ActiveDockable);
            dock.DefaultDockable = Resolve(dock.DefaultDockable);
            dock.FocusedDockable = Resolve(dock.FocusedDockable);
        }

        IDockable? Resolve(IDockable? reference) => reference?.Id is { } id && byId.TryGetValue(id, out IDockable? canonical)
            ? canonical : null;
    }

    private static bool HasDesktopLifetime() =>
        Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;

    // The references are read before any collection changes and cleared after all of them: removing the active
    // document from its dock makes Dock activate the next document, which also moves the root focus onto it, and a
    // reference cleared before that would be written into the layout again.
    private static List<Action> DetachRuntimeDocuments(IRootDock rootDock)
    {
        List<Action> restoreCollections = new();
        List<Action> restoreReferences = new();
        List<(Func<IDockable?> Get, Action<IDockable?> Set)> references = new();
        IReadOnlyList<IDock> docks = FindDockables<IDock>(rootDock);
        foreach (IDock dock in docks)
        {
            CaptureReference(() => dock.ActiveDockable, value => dock.ActiveDockable = value);
            CaptureReference(() => dock.DefaultDockable, value => dock.DefaultDockable = value);
            CaptureReference(() => dock.FocusedDockable, value => dock.FocusedDockable = value);
        }

        foreach (IDock dock in docks)
        {
            DetachCollection(dock.VisibleDockables);
            if (dock is IRootDock root)
            {
                DetachCollection(root.HiddenDockables);
                DetachCollection(root.LeftPinnedDockables);
                DetachCollection(root.RightPinnedDockables);
                DetachCollection(root.TopPinnedDockables);
                DetachCollection(root.BottomPinnedDockables);
            }
        }

        foreach ((Func<IDockable?> get, Action<IDockable?> set) in references)
        {
            if (get() is DesignerDocument or TextDocument or ExtensionDocument)
            {
                set(null);
            }
        }

        restoreCollections.AddRange(restoreReferences);
        return restoreCollections;

        void CaptureReference(Func<IDockable?> get, Action<IDockable?> set)
        {
            IDockable? value = get();
            references.Add((get, set));
            if (value is DesignerDocument or TextDocument or ExtensionDocument)
            {
                restoreReferences.Add(() => set(value));
            }
        }

        void DetachCollection(IList<IDockable>? items)
        {
            if (items is null) return;
            var documents = items.Select((item, index) => (Item: item, Index: index))
                .Where(entry => entry.Item is DesignerDocument or TextDocument or ExtensionDocument).ToArray();
            for (int index = documents.Length - 1; index >= 0; index--)
                items.RemoveAt(documents[index].Index);
            if (documents.Length == 0) return;
            restoreCollections.Add(() =>
            {
                foreach (var document in documents)
                    items.Insert(Math.Min(document.Index, items.Count), document.Item);
            });
        }
    }

    private static List<Action> DetachDockRuntimeReferences(IRootDock rootDock)
    {
        List<Action> restore = new();

        if (rootDock.PinnedDock is { } pinnedDock)
        {
            rootDock.PinnedDock = null;
            restore.Add(() => rootDock.PinnedDock = pinnedDock);
        }

        if (rootDock.Window is not null)
        {
            IDockWindow? window = rootDock.Window;
            rootDock.Window = null;
            restore.Add(() => rootDock.Window = window);
        }

        if (rootDock.Windows is not null)
        {
            IList<IDockWindow>? windows = rootDock.Windows;
            rootDock.Windows = null;
            restore.Add(() => rootDock.Windows = windows);
        }

        DetachOwnersRecursive(rootDock, restore);
        DetachFactoryRecursive(rootDock, restore);
        return restore;
    }

    private static void DetachFactoryRecursive(IDockable dockable, List<Action> restore)
    {
        if (dockable is IDock dock && dock.Factory is not null)
        {
            IFactory? factory = dock.Factory;
            dock.Factory = null;
            restore.Add(() => dock.Factory = factory);
        }

        if (dockable is not IDock dockWithChildren || dockWithChildren.VisibleDockables is null)
        {
            return;
        }

        foreach (IDockable child in dockWithChildren.VisibleDockables)
        {
            DetachFactoryRecursive(child, restore);
        }

        if (dockable is IRootDock rootDock)
        {
            DetachFactoryList(rootDock.HiddenDockables, restore);
            DetachFactoryList(rootDock.LeftPinnedDockables, restore);
            DetachFactoryList(rootDock.RightPinnedDockables, restore);
            DetachFactoryList(rootDock.TopPinnedDockables, restore);
            DetachFactoryList(rootDock.BottomPinnedDockables, restore);
            if (rootDock.PinnedDock is not null)
            {
                DetachFactoryRecursive(rootDock.PinnedDock, restore);
            }
        }
    }

    private static void DetachFactoryList(IList<IDockable>? dockables, List<Action> restore)
    {
        if (dockables is null)
        {
            return;
        }

        foreach (IDockable dockable in dockables)
        {
            DetachFactoryRecursive(dockable, restore);
        }
    }

    private static void DetachOwnersRecursive(IDockable dockable, List<Action> restore)
    {
        if (dockable.Owner is not null)
        {
            IDockable? owner = dockable.Owner;
            dockable.Owner = null;
            restore.Add(() => dockable.Owner = owner);
        }

        if (dockable is not IDock dock || dock.VisibleDockables is null)
        {
            return;
        }

        foreach (IDockable child in dock.VisibleDockables)
        {
            DetachOwnersRecursive(child, restore);
        }

        if (dock is IRootDock rootDock)
        {
            DetachOwnersList(rootDock.HiddenDockables, restore);
            DetachOwnersList(rootDock.LeftPinnedDockables, restore);
            DetachOwnersList(rootDock.RightPinnedDockables, restore);
            DetachOwnersList(rootDock.TopPinnedDockables, restore);
            DetachOwnersList(rootDock.BottomPinnedDockables, restore);
            if (rootDock.PinnedDock is not null)
            {
                DetachOwnersRecursive(rootDock.PinnedDock, restore);
            }
        }
    }

    private static void DetachOwnersList(IList<IDockable>? dockables, List<Action> restore)
    {
        if (dockables is null)
        {
            return;
        }

        foreach (IDockable dockable in dockables)
        {
            DetachOwnersRecursive(dockable, restore);
        }
    }

    private void SetOwnerRecursive(IDockable dockable, IDockable? owner)
    {
        if (dockable.Owner is null)
        {
            dockable.Owner = owner;
        }

        dockable.Factory ??= this;
        dockable.DockCapabilityOverrides ??= new DockCapabilityOverrides();

        if (dockable is IDock dock)
        {
            dock.DockCapabilityPolicy ??= new DockCapabilityPolicy();
            dock.Factory ??= this;
            if (dock.VisibleDockables is not null)
            {
                foreach (IDockable child in dock.VisibleDockables)
                {
                    SetOwnerRecursive(child, dockable);
                }
            }
        }

        if (dockable is IRootDock rootDock)
        {
            rootDock.RootDockCapabilityPolicy ??= new DockCapabilityPolicy();
            SetOwnerList(rootDock.HiddenDockables, rootDock);
            SetOwnerList(rootDock.LeftPinnedDockables, rootDock);
            SetOwnerList(rootDock.RightPinnedDockables, rootDock);
            SetOwnerList(rootDock.TopPinnedDockables, rootDock);
            SetOwnerList(rootDock.BottomPinnedDockables, rootDock);
            if (rootDock.PinnedDock is not null)
            {
                SetOwnerRecursive(rootDock.PinnedDock, rootDock);
            }
        }
    }

    private void SetOwnerList(IList<IDockable>? dockables, IDockable owner)
    {
        if (dockables is null)
        {
            return;
        }

        foreach (IDockable dockable in dockables)
        {
            SetOwnerRecursive(dockable, owner);
        }
    }
}
