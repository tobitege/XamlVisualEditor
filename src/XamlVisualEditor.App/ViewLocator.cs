using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Model.Core;
using XamlVisualEditor.AcpExtension;
using XamlVisualEditor.AcpExtension.Views;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.CodeEditor;
using XamlVisualEditor.Collaboration.UI;
using XamlVisualEditor.CollaborationExtension;
using XamlVisualEditor.CollaborationExtension.Views;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Views;
using XamlVisualEditor.IdeBridgeExtension;
using XamlVisualEditor.IdeBridgeExtension.Views;
using XamlVisualEditor.LspSettingsExtension;
using XamlVisualEditor.LspSettingsExtension.Views;
using XamlVisualEditor.McpExtension;
using XamlVisualEditor.McpExtension.Views;
using XamlVisualEditor.NavigationExtension;
using XamlVisualEditor.NavigationExtension.Views;
using XamlVisualEditor.OutputExtension;
using XamlVisualEditor.OutputExtension.Views;
using XamlVisualEditor.PropertyEditorExtension;
using XamlVisualEditor.PropertyEditorExtension.Views;
using XamlVisualEditor.ToolboxExtension;
using XamlVisualEditor.ToolboxExtension.Views;
using XamlVisualEditor.DebugSettingsExtension;
using XamlVisualEditor.DebugSettingsExtension.Views;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Terminal.Avalonia.Views;
using XamlVisualEditor.TfvcExtension.ViewModels;
using XamlVisualEditor.TfvcExtension.Views;
using XamlVisualEditor.TreeInspectorExtension;
using XamlVisualEditor.TreeInspectorExtension.Views;
using XamlVisualEditor.TreeView;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.App;

/// <summary>
/// Non-reflection ViewLocator that maps ViewModel types to View types.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    /// <inheritdoc />
    public Control Build(object? param)
    {
        if (param is IDockable dockable)
        {
            Control? dockView = dockable switch
            {
                DesignerDocument doc => new DesignerDocumentView { DataContext = doc.DocumentViewModel },
                TextDocument doc => new TextFileView { DataContext = doc.DocumentViewModel },
                InfiniteCanvasDocument doc => new InfiniteCanvasView { DataContext = doc.CanvasViewModel },
                CanvasMdiDocument doc when doc.DocumentViewModel is DesignerDocumentViewModel designer =>
                    new DesignerDocumentView { DataContext = designer },
                CanvasMdiDocument doc when doc.DocumentViewModel is TextDocumentViewModel text =>
                    new TextFileView { DataContext = text },
                SolutionExplorerTool tool => new SolutionExplorerView { DataContext = tool.SolutionExplorerViewModel },
                OutputTool tool => new OutputView { DataContext = tool.OutputViewModel },
                TerminalTool tool => new TerminalToolView { DataContext = tool },
                BreakpointsTool tool => new BreakpointsView { DataContext = tool.BreakpointsViewModel },
                CallStackTool tool => new CallStackView { DataContext = tool.CallStackViewModel },
                LocalsTool tool => new LocalsView { DataContext = tool.LocalsViewModel },
                WatchesTool tool => new WatchesView { DataContext = tool.WatchesViewModel },
                ExtensionTool tool => new ExtensionToolView { DataContext = tool },
                _ => null
            };

            if (dockView is not null)
            {
                return ApplyEditorDensity(dockView);
            }
        }

        return ApplyEditorDensity(param switch
        {
            DesignerDocumentViewModel => new DesignerDocumentView(),
            SolutionExplorerViewModel => new SolutionExplorerView(),
            ToolboxViewModel => new ToolboxView(),
            OutputViewModel => new OutputView(),
            AcpToolViewModel => new AcpToolView(),
            DebugSettingsViewModel => new DebugSettingsView(),
            LspSettingsViewModel => new LspSettingsView(),
            DebugSettingsPanelViewModel => new DebugSettingsPanelView(),
            LspSettingsPanelViewModel => new LspSettingsPanelView(),
            BreakpointsViewModel => new BreakpointsView(),
            CallStackViewModel => new CallStackView(),
            LocalsViewModel => new LocalsView(),
            WatchesViewModel => new WatchesView(),
            CodeEditorViewModel => new XamlCodeEditorView(),
            TextDocumentViewModel => new TextFileView(),
            InfiniteCanvasViewModel => new InfiniteCanvasView(),
            DesignSurfaceViewModel => new DesignSurfaceView(),
            CollaborationPanelToolViewModel => new CollaborationToolPanelView(),
            CollaborationPanelViewModel => new CollaborationPanelView(),
            VisualTreeNodeViewModel => new VisualTreePanelView(),
            LogicalTreeNodeViewModel => new LogicalTreePanelView(),
            AnimationEditorViewModel => new AnimationEditorView(),
            TerminalViewModel => new TerminalView(),
            GitPanelViewModel => new GitPanelView(),
            PendingChangesViewModel => new PendingChangesView(),
            SourceControlExplorerViewModel => new SourceControlExplorerView(),
            TfvcHistoryViewModel => new TfvcHistoryView(),
            IdeBridgePanelViewModel => new IdeBridgePanelView(),
            McpPanelViewModel => new McpPanelView(),
            ReferencesPanelViewModel => new ReferencesPanelView(),
            OutputPanelViewModel => new OutputPanelView(),
            ProblemsPanelViewModel => new ProblemsPanelView(),
            PropertyEditorPanelViewModel => new PropertyEditorPanelView(),
            ToolboxPanelViewModel => new ToolboxPanelView(),
            TreeInspectorPanelViewModel => new TreeInspectorPanelView(),
            ExtensionTreeViewModel => new ExtensionTreeView(),
            ExtensionWebviewViewModel => new ExtensionWebviewView(),
            ExtensionManagerViewModel => new ExtensionManagerView(),
            _ => new TextBlock { Text = string.Format(UiStrings.MissingView, (object?)(param?.GetType().FullName ?? "null")) }
        });
    }

    private static Control ApplyEditorDensity(Control view)
    {
        // A document's controls must keep their own theme metrics.
        if (view is not DesignerDocumentView and not DesignSurfaceView and not InfiniteCanvasView)
        {
            view.Classes.Add("editor-chrome");
        }

        return view;
    }

    /// <inheritdoc />
    public bool Match(object? data)
    {
        // Match known ViewModel types
        return data is IDockable
            or DesignerDocumentViewModel
            or SolutionExplorerViewModel
            or ToolboxViewModel
            or OutputViewModel
            or AcpToolViewModel
            or DebugSettingsViewModel
            or LspSettingsViewModel
            or DebugSettingsPanelViewModel
            or LspSettingsPanelViewModel
            or BreakpointsViewModel
            or CallStackViewModel
            or LocalsViewModel
            or WatchesViewModel
            or ReferencesViewModel
            or ReferencesPanelViewModel
            or CodeEditorViewModel
            or TextDocumentViewModel
            or InfiniteCanvasViewModel
            or DesignSurfaceViewModel
            or CollaborationPanelToolViewModel
            or CollaborationPanelViewModel
            or VisualTreeNodeViewModel
            or LogicalTreeNodeViewModel
            or AnimationEditorViewModel
            or TerminalViewModel
            or GitPanelViewModel
            or PendingChangesViewModel
            or SourceControlExplorerViewModel
            or TfvcHistoryViewModel
            or IdeBridgePanelViewModel
            or McpPanelViewModel
            or OutputPanelViewModel
            or ProblemsPanelViewModel
            or PropertyEditorPanelViewModel
            or ToolboxPanelViewModel
            or TreeInspectorPanelViewModel
            or ExtensionTreeViewModel
            or ExtensionWebviewViewModel
            or ExtensionManagerViewModel;
    }
}
