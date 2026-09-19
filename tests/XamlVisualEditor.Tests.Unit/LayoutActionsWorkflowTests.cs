using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
            document.DesignSurface.SelectNodesFromSync(selected);
            new DesignerLayoutEditor(document).Apply(DesignerLayoutOperation.DistributeHorizontally);
            await FinishLayoutAsync(host);
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

    private static async Task FinishLayoutAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
