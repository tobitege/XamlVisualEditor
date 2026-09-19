using System;
using System.IO;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignerSurfaceWorkflowTests
{
    [AvaloniaTheory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Surface_rebuild_duplicate_undo_and_save_preserve_template_structure(bool failingSibling)
    {
        string xaml = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:library="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="480" Height="260">
              <StackPanel>
                <Button x:Name="Action" Content="Run">
                  <Button.Template>
                    <ControlTemplate>
                      <Border Name="PART_Frame" Background="Tomato">
                        <TextBlock Text="{TemplateBinding Content}" />
                      </Border>
                    </ControlTemplate>
                  </Button.Template>
                </Button>
                <!-- optional failing sibling -->
              </StackPanel>
            </UserControl>
            """;
        xaml = xaml.Replace("<!-- optional failing sibling -->", failingSibling ? "<library:ConstructorTrapControl />" : string.Empty, StringComparison.Ordinal);
        string path = Path.Combine(Path.GetTempPath(), "xve-surface-" + Guid.NewGuid().ToString("N") + ".axaml");
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, xaml);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        metadata.LoadAssembly(typeof(XamlVisualEditor.TestControls.LibraryGrid).Assembly.Location);
        using DesignerDocumentViewModel document = new(path, metadata);
        await document.LoadAsync();
        XamlSerializationService serializer = new();
        string original = serializer.Serialize(document.SyncEngine.CurrentDocument!);
        DesignSurfaceView view = new() { DataContext = document.DesignSurface };
        Border documentHost = new() { DataContext = document, Child = view };
        Window host = new() { Content = documentHost, Width = 800, Height = 600 };
        try
        {
            host.Show();
            await FinishLayoutAsync(host);
            Assert.Equal(original, serializer.Serialize(document.SyncEngine.CurrentDocument!));
            Assert.NotEmpty(document.DesignSurface.ControlMap);
            MutableAstObjectNode button = Assert.Single(document.DesignSurface.ItemMap.Values,
                item => item.AstNode.TypeName == "Button").AstNode;
            MutableAstObjectNode templateBorder = Assert.Single(document.DesignSurface.ItemMap.Values,
                item => item.AstNode.TypeName == "Border").AstNode;
            Assert.NotSame(button, templateBorder.Parent);

            document.DesignSurface.SelectByAstNodeId(button.Id);
            Assert.Equal(button.Id, document.SelectedNodeId);
            var copies = new DesignerSelectionEditor(document.SyncEngine, metadata).Duplicate(new[] { button });
            MutableAstObjectNode copy = Assert.Single(copies);
            document.DesignSurface.RequestSelection(new[] { copy.Id });
            await FinishLayoutAsync(host);
            Assert.Equal(2, document.DesignSurface.ItemMap.Values.Count(item => item.AstNode.TypeName == "Button"));
            Assert.Contains(copy.Id, document.DesignSurface.ItemMap.Keys);

            document.SyncEngine.Undo();
            await FinishLayoutAsync(host);
            Assert.Equal(original, serializer.Serialize(document.SyncEngine.CurrentDocument!));
            Assert.Single(document.DesignSurface.ItemMap.Values, item => item.AstNode.TypeName == "Button");
            Assert.Contains(button.Id, document.DesignSurface.ItemMap.Keys);
            Assert.DoesNotContain(copy.Id, document.DesignSurface.ItemMap.Keys);

            await document.SaveCommand.Execute().ToTask();
            string saved = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("xveDesign", saved, StringComparison.Ordinal);
            var reopened = new XamlParsingService().Parse(saved);
            Assert.False(reopened.HasErrors);
            Assert.Equal(original, serializer.Serialize(Assert.IsType<MutableAstDocument>(reopened.Document)));
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
