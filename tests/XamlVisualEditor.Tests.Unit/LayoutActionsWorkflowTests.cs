using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class LayoutActionsWorkflowTests
{
    [AvaloniaTheory(Timeout = 30000)]
    [InlineData("Canvas")]
    [InlineData("Grid")]
    [InlineData("StackPanel")]
    public async Task Distribution_uses_native_layout_and_undo_restores_source(string layout)
    {
        string attributes = layout switch
        {
            "Grid" => "ColumnDefinitions=\"Auto,2*,*\"",
            "StackPanel" => "Orientation=\"Horizontal\"",
            _ => string.Empty
        };
        string placement = layout == "Canvas" ? "Canvas.Left=\"{0}\"" : string.Empty;
        string children = string.Join(string.Empty, new[] { 40, 100, 250 }.Select(left =>
            $"<Button Width=\"50\" Height=\"30\" {string.Format(CultureInfo.InvariantCulture, placement, left)} />"));
        string xaml = $"<UserControl xmlns=\"https://github.com/avaloniaui\" Width=\"600\" Height=\"300\"><{layout} {attributes}>{children}</{layout}></UserControl>";
        string path = Path.Combine(Path.GetTempPath(), "xve-layout-" + Guid.NewGuid().ToString("N") + ".axaml");
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, xaml);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        using DesignerDocumentViewModel document = new(path, metadata);
        await document.LoadAsync();
        XamlSerializationService serializer = new();
        string original = serializer.Serialize(document.SyncEngine.CurrentDocument!);
        DesignSurfaceView view = new() { DataContext = document.DesignSurface };
        Window host = new() { Content = new Border { DataContext = document, Child = view }, Width = 800, Height = 600 };
        try
        {
            host.Show();
            await FinishLayoutAsync(host);
            Guid[] selected = document.DesignSurface.ItemMap.Values.Where(item => item.AstNode.TypeName == "Button")
                .Select(item => item.AstNodeId).ToArray();
            Assert.Equal(3, selected.Length);
            Control[] originalControls = selected.Select(id => document.DesignSurface.ItemMap[id].VisualElement!).ToArray();
            document.DesignSurface.SelectNodesFromSync(selected);
            new DesignerLayoutEditor(document).Apply(DesignerLayoutOperation.DistributeHorizontally);
            await FinishLayoutAsync(host);
            for (int index = 0; index < selected.Length; index++)
                Assert.Same(originalControls[index], document.DesignSurface.ItemMap[selected[index]].VisualElement);
            MutableAstObjectNode parent = Assert.Single(document.SyncEngine.CurrentDocument!.Root!.Children.OfType<MutableAstObjectNode>());
            MutableAstObjectNode[] nodes = parent.Children.OfType<MutableAstObjectNode>().ToArray();
            if (layout == "Canvas") Assert.Equal("145", nodes[1].GetPropertyValue("Canvas.Left"));
            if (layout == "Grid")
            {
                Assert.Equal("Auto,2*,*", parent.GetPropertyValue("ColumnDefinitions"));
                Assert.Equal(new[] { "0", "1", "2" }, nodes.Select(node => node.GetPropertyValue("Grid.Column")));
            }
            if (layout == "StackPanel") Assert.Equal("225", parent.GetPropertyValue("Spacing"));
            if (layout != "Canvas") Assert.All(nodes, node => Assert.Null(node.GetPropertyValue("Canvas.Left")));

            document.SyncEngine.Undo();
            await FinishLayoutAsync(host);
            Assert.Equal(original, serializer.Serialize(document.SyncEngine.CurrentDocument!));
            Assert.All(selected, id => Assert.Contains(id, document.DesignSurface.ItemMap.Keys));
        }
        finally { host.Close(); }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Align_left_in_grid_moves_edges_to_the_primary_control_via_margins()
    {
        string xaml = "<UserControl xmlns=\"https://github.com/avaloniaui\" Width=\"600\" Height=\"300\"><Grid ColumnDefinitions=\"300,*\">"
            + "<Button Width=\"50\" Height=\"30\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\" Margin=\"20,10,0,0\" />"
            + "<Button Grid.Column=\"1\" Width=\"50\" Height=\"30\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\" Margin=\"40,60,0,0\" />"
            + "<Button Width=\"50\" Height=\"30\" HorizontalAlignment=\"Right\" VerticalAlignment=\"Top\" Margin=\"0,110,30,0\" />"
            + "</Grid></UserControl>";
        string path = Path.Combine(Path.GetTempPath(), "xve-layout-" + Guid.NewGuid().ToString("N") + ".axaml");
        await File.WriteAllTextAsync(path, xaml);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        using DesignerDocumentViewModel document = new(path, metadata);
        await document.LoadAsync();
        DesignSurfaceView view = new() { DataContext = document.DesignSurface };
        Window host = new() { Content = new Border { DataContext = document, Child = view }, Width = 800, Height = 600 };
        try
        {
            host.Show();
            await FinishLayoutAsync(host);
            MutableAstObjectNode grid = Assert.Single(document.SyncEngine.CurrentDocument!.Root!.Children.OfType<MutableAstObjectNode>());
            Guid[] selected = grid.Children.OfType<MutableAstObjectNode>().Select(node => node.Id).ToArray();
            document.DesignSurface.SelectNodesFromSync(selected);
            Assert.Equal(selected[0], ((XamlVisualEditor.Designer.Core.DesignItem)document.DesignSurface.Selection.PrimarySelection!).AstNodeId);
            Control[] originalControls = selected.Select(id => document.DesignSurface.ItemMap[id].VisualElement!).ToArray();
            Border progress = view.FindControl<Border>("RebuildProgressOverlay")!;
            bool progressShown = false;
            int selectionChanges = 0;
            document.DesignSurface.Selection.SelectionChanged += _ => selectionChanges++;
            System.Collections.Generic.List<int> codeEditOffsets = new();
            document.CodeEditor.Document.Changed += (_, change) => codeEditOffsets.Add(change.Offset);
            document.DesignSurface.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(document.DesignSurface.ShowRebuildProgress)
                    && document.DesignSurface.ShowRebuildProgress) progressShown = true;
            };

            new DesignerLayoutEditor(document).Apply(DesignerLayoutOperation.AlignLeft);
            await FinishLayoutAsync(host);
            Assert.Single(codeEditOffsets);
            Assert.True(codeEditOffsets[0] > 0, "Designer edits should update only the changed XAML span.");
            Assert.Equal(0, selectionChanges);
            Assert.False(progressShown);
            Assert.False(progress.IsVisible);
            for (int index = 0; index < selected.Length; index++)
                Assert.Same(originalControls[index], document.DesignSurface.ItemMap[selected[index]].VisualElement);

            MutableAstObjectNode[] nodes = grid.Children.OfType<MutableAstObjectNode>().ToArray();
            Assert.Equal("20,10,0,0", nodes[0].GetPropertyValue("Margin"));
            Assert.Equal("-280,60,0,0", nodes[1].GetPropertyValue("Margin"));
            Assert.Equal("0,110,230,0", nodes[2].GetPropertyValue("Margin"));
            Assert.All(nodes, node => Assert.Equal(node == nodes[2] ? "Right" : "Left", node.GetPropertyValue("HorizontalAlignment")));

            Control gridControl = document.DesignSurface.ItemMap[grid.Id].VisualElement!;
            double[] lefts = selected.Select(id => document.DesignSurface.ItemMap[id].VisualElement!.TranslatePoint(default, gridControl)!.Value.X).ToArray();
            Assert.True(lefts.All(left => Math.Abs(left - 20) < 0.001), string.Join(",", lefts));
            Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
            document.DesignSurface.RequestRebuild();
            Assert.True(progress.IsVisible);
            await FinishLayoutAsync(host);
            Assert.False(progress.IsVisible);
            int beforeNewSelection = selectionChanges;
            document.DesignSurface.SelectNodesFromSync(new[] { selected[1] });
            Assert.Equal(selected[1], ((XamlVisualEditor.Designer.Core.DesignItem)document.DesignSurface.Selection.PrimarySelection!).AstNodeId);
            Assert.True(selectionChanges > beforeNewSelection);
        }
        finally { host.Close(); }
    }

    private static async Task FinishLayoutAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
