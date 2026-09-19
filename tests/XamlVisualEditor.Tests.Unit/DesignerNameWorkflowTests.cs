using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.PropertyEditorExtension;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignerNameWorkflowTests
{
    [AvaloniaFact(Timeout = 30000)]
    public async Task RenameUpdatesStyleNameSelectorsWithoutChangingTagLiterals()
    {
        const string source = """
            <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Window.Styles>
                <Style Selector="TextBlock#Source"><Setter Property="Foreground" Value="Red" /></Style>
                <Style Selector="TextBlock[Name=Source]"><Setter Property="FontSize" Value="23" /></Style>
                <Style Selector="TextBlock[Tag='#Source']"><Setter Property="Opacity" Value="0.5" /></Style>
              </Window.Styles>
              <TextBlock x:Name="Source" Text="Styled control" />
            </Window>
            """;
        string path = Path.Combine(Path.GetTempPath(), "xve-selector-name-" + Guid.NewGuid().ToString("N") + ".axaml");
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, source);
        TypeMetadataService metadata = new();
        using DesignerDocumentViewModel document = new(path, metadata);
        await document.LoadAsync();
        using MainWindowViewModel main = new(metadataService: metadata);
        main.Documents.Add(document);
        main.ActiveDocument = document;
        using ShellDesignerHost host = new(main);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        Guid sourceId = Assert.Single(root.Children).Id;
        Assert.True(await host.SetPropertyAsync(sourceId.ToString("D"), "Name", "RenamedSource", CancellationToken.None));
        string text = new XamlSerializationService().Serialize(document.SyncEngine.CurrentDocument);
        Assert.Contains("TextBlock#RenamedSource", text);
        Assert.Contains("TextBlock[Name=RenamedSource]", text);
        Assert.Contains("TextBlock[Tag='#Source']", text);
        RuntimeDesignRenderResult rendered = new RuntimeDesignRenderer(metadata).Render(document.SyncEngine.CurrentDocument, new Uri(path));
        Window window = Assert.IsType<Window>(rendered.Root);
        try
        {
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            TextBlock control = Assert.IsType<TextBlock>(window.Content);
            Assert.Equal(23, control.FontSize);
            Assert.Equal(Avalonia.Media.Colors.Red, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(control.Foreground).Color);
            Assert.Equal(1, control.Opacity);
            Assert.Empty(rendered.Diagnostics);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task InspectorNameRenameUpdatesBindingsAtomicallyAndPreservesTemplateNamesAndLiterals()
    {
        const string source = """
            <Window xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel>
                <TextBox x:Name="Source" Text="value" />
                <TextBlock x:Name="ByPath" Text="{Binding #Source.Text}" />
                <TextBlock x:Name="ByElement" Text="{Binding Text, ElementName='Source', StringFormat='ElementName=Source'}" />
                <TextBlock x:Name="ByObject"><TextBlock.Text><Binding ElementName="Source" Path="Text" /></TextBlock.Text></TextBlock>
                <TextBlock x:Name="Literal" Text="{}{Binding #Source.Text}" />
                <TextBox x:Name="Taken" />
                <Button><Button.Template><ControlTemplate><StackPanel>
                  <TextBox x:Name="Source" />
                  <TextBlock Text="{Binding #Source.Text}" />
                </StackPanel></ControlTemplate></Button.Template></Button>
              </StackPanel>
            </Window>
            """;
        string path = Path.Combine(Path.GetTempPath(), "xve-name-workflow-" + Guid.NewGuid().ToString("N") + ".axaml");
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, source);
        TypeMetadataService metadata = new();
        using DesignerDocumentViewModel document = new(path, metadata);
        await document.LoadAsync();
        using MainWindowViewModel main = new(metadataService: metadata);
        main.Documents.Add(document);
        main.ActiveDocument = document;
        MutableAstObjectNode panel = Assert.IsType<MutableAstObjectNode>(Assert.Single(document.SyncEngine.CurrentDocument!.Root!.Children));
        Guid sourceId = panel.Children[0].Id;
        document.SetSelectedNode(sourceId, SyncSource.TreeView);
        using ShellDesignerHost host = new(main);
        using PropertyEditorPanelViewModel inspector = new(host, new PropertyEditorRegistry());
        await inspector.InitializeAsync(CancellationToken.None);
        PropertyEntryViewModel name = Assert.Single(inspector.PropertiesView.Cast<PropertyEntryViewModel>(), entry => entry.Name == "Name");
        Assert.Equal("Source", name.Value);
        Assert.DoesNotContain(inspector.PropertiesView.Cast<PropertyEntryViewModel>(), entry => entry.Name == "x:Name");
        XamlSerializationService serializer = new();
        string before = serializer.Serialize(document.SyncEngine.CurrentDocument);
        Assert.True(await host.SetPropertyAsync(sourceId.ToString("D"), "Name", "RenamedSource", CancellationToken.None));
        string after = serializer.Serialize(document.SyncEngine.CurrentDocument);
        XDocument saved = XDocument.Parse(after);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Declaration(string identifier) => Assert.Single(saved.Descendants(), element => (string?)element.Attribute(x + "Name") == identifier);
        Assert.Null(Declaration("RenamedSource").Attribute("Name"));
        Assert.Equal("{Binding #RenamedSource.Text}", Declaration("ByPath").Attribute("Text")?.Value);
        Assert.Equal("{Binding Text, ElementName='RenamedSource', StringFormat='ElementName=Source'}", Declaration("ByElement").Attribute("Text")?.Value);
        Assert.Equal("RenamedSource", Assert.Single(saved.Descendants(), element => element.Name.LocalName == "Binding").Attribute("ElementName")?.Value);
        Assert.Equal("{}{Binding #Source.Text}", Declaration("Literal").Attribute("Text")?.Value);
        XElement template = Assert.Single(saved.Descendants(), element => element.Name.LocalName == "ControlTemplate");
        Assert.Contains(template.Descendants(), element => (string?)element.Attribute(x + "Name") == "Source");
        Assert.Contains(template.Descendants(), element => (string?)element.Attribute("Text") == "{Binding #Source.Text}");

        document.SyncEngine.Undo();
        Assert.Equal(before, serializer.Serialize(document.SyncEngine.CurrentDocument!));
        document.SyncEngine.Redo();
        Assert.Equal(after, serializer.Serialize(document.SyncEngine.CurrentDocument!));
        foreach (string invalid in new[] { "Taken", "1invalid", "invalid name" })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetPropertyAsync(sourceId.ToString("D"), "Name", invalid, CancellationToken.None));
            Assert.Equal(after, serializer.Serialize(document.SyncEngine.CurrentDocument!));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetPropertyAsync(sourceId.ToString("D"), "Name", null, CancellationToken.None));
        Assert.Equal(after, serializer.Serialize(document.SyncEngine.CurrentDocument!));

        RuntimeDesignRenderResult rendered = new RuntimeDesignRenderer(metadata).Render(document.SyncEngine.CurrentDocument!, new Uri(path));
        Window window = Assert.IsType<Window>(rendered.Root);
        try
        {
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            StackPanel content = Assert.IsType<StackPanel>(window.Content);
            Assert.Equal("value", Assert.IsType<TextBlock>(content.Children[1]).Text);
            Assert.Equal("value", Assert.IsType<TextBlock>(content.Children[3]).Text);
            Assert.Empty(rendered.Diagnostics);
        }
        finally { window.Close(); }
    }
}
