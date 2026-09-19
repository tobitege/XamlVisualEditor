using System;
using System.IO;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.PropertyEditorExtension;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Serialization;
using EditorFeatures = XamlVisualEditor.TestControls.EditorFeatures;
using PropertyFixtureControl = XamlVisualEditor.TestControls.PropertyFixtureControl;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PropertyEditingWorkflowTests
{
    [AvaloniaTheory(Timeout = 30000)]
    [InlineData("library:PropertyFixtureControl")]
    [InlineData("CheckBox")]
    public async Task Inspector_edits_empty_reset_nullable_flags_and_invalid_drafts_and_tracks_undo(string propertyOwner)
    {
        string xaml = """
            <library:PropertyFixtureControl xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:library="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls"
                Features="Alpha" IsThreeState="True" IsChecked="{x:Null}" Padding="1,2,3,4">
              <OWNER.Content>seed</OWNER.Content>
              <OWNER.Tag><Binding Path="Caption" /></OWNER.Tag>
              <!--template note-->
              <OWNER.Template><ControlTemplate><Border Background="Tomato" /></ControlTemplate></OWNER.Template>
            </library:PropertyFixtureControl>
            """;
        xaml = xaml.Replace("OWNER", propertyOwner, StringComparison.Ordinal);
        string ownerName = propertyOwner[(propertyOwner.IndexOf(':') + 1)..];
        string path = Path.Combine(Path.GetTempPath(), "xve-properties-" + Guid.NewGuid().ToString("N") + ".axaml");
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, xaml);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        metadata.LoadAssembly(typeof(PropertyFixtureControl).Assembly.Location);
        using DesignerDocumentViewModel document = new(path, metadata);
        await document.LoadAsync();
        using MainWindowViewModel main = new(metadataService: metadata);
        main.Documents.Add(document);
        main.ActiveDocument = document;
        document.SetSelectedNode(document.SyncEngine.CurrentDocument!.Root!.Id, SyncSource.TreeView);
        using ShellDesignerHost host = new(main);
        using PropertyEditorPanelViewModel inspector = new(host, new PropertyEditorRegistry());
        await inspector.InitializeAsync(CancellationToken.None);
        PropertyEntryViewModel Entry(string name) => Assert.Single(inspector.PropertiesView.Cast<PropertyEntryViewModel>(), entry => entry.Name == name);

        PropertyEntryViewModel content = Entry("Content");
        Assert.Equal("seed", content.Value);
        Assert.DoesNotContain(inspector.PropertiesView.Cast<PropertyEntryViewModel>(), entry => entry.Name == ownerName + ".Content");
        content.Value = string.Empty;
        await content.CommitCommand.Execute().ToTask();
        Assert.Equal(string.Empty, document.SyncEngine.CurrentDocument!.Root!.GetPropertyValue(ownerName + ".Content"));
        Assert.True(content.IsSet);
        await content.ResetCommand.Execute().ToTask();
        Assert.Null(document.SyncEngine.CurrentDocument.Root.FindProperty(ownerName + ".Content"));
        Assert.False(content.IsSet);

        PropertyEntryViewModel nullable = Entry("IsChecked");
        Assert.Equal("{x:Null}", nullable.Value);
        nullable.BoolValue = true;
        await FlushAsync();
        Assert.Equal("True", document.SyncEngine.CurrentDocument.Root.GetPropertyValue("IsChecked"));
        PropertyEntryViewModel flags = Entry("Features");
        Assert.True(flags.IsFlagsEnum);
        Assert.Single(flags.FlagOptions, option => option.Name == "High").IsChecked = true;
        await FlushAsync();
        Assert.Equal(EditorFeatures.High | EditorFeatures.Alpha,
            Enum.Parse<EditorFeatures>(document.SyncEngine.CurrentDocument.Root.GetPropertyValue("Features")!));

        PropertyEntryViewModel padding = Entry("Padding");
        padding.Value = "invalid thickness";
        await padding.CommitCommand.Execute().ToTask();
        Assert.True(padding.HasValidationError);
        Assert.Equal("invalid thickness", padding.Value);
        Assert.Equal("1,2,3,4", document.SyncEngine.CurrentDocument.Root.GetPropertyValue("Padding"));
        padding.Value = "8,9";
        await padding.CommitCommand.Execute().ToTask();
        Assert.False(padding.HasValidationError);
        Assert.Null(inspector.StatusText);
        Assert.Equal("8,9", document.SyncEngine.CurrentDocument.Root.GetPropertyValue("Padding"));

        document.SyncEngine.Undo();
        await FlushAsync();
        Assert.Equal("1,2,3,4", document.SyncEngine.CurrentDocument.Root.GetPropertyValue("Padding"));
        Assert.Equal("1,2,3,4", Entry("Padding").Value);
        document.SyncEngine.Redo();
        await FlushAsync();
        Assert.Equal("8,9", Entry("Padding").Value);
        PropertyEntryViewModel tag = Entry("Tag");
        Assert.True(tag.IsSet);
        await tag.ResetCommand.Execute().ToTask();
        Assert.Null(document.SyncEngine.CurrentDocument.Root.FindProperty(ownerName + ".Tag"));
        document.SyncEngine.Undo();
        await FlushAsync();
        Assert.True(Entry("Tag").IsSet);
        Assert.Equal(PropertyEditorKind.Template, Entry("Template").EditorKind);
        Assert.True(Entry("Template").HasStructureEditor);
        DesignerPropertyStructure snapshot = await host.OpenPropertyStructureAsync(
            document.SyncEngine.CurrentDocument.Root.Id.ToString("D"), "Template", CancellationToken.None);
        using PropertyStructureEditorViewModel structure = new(host, snapshot);
        structure.SelectedNode = Assert.Single(structure.Roots.SelectMany(node => node.DescendantsAndSelf()), node => node.Label == "Border");
        structure.AttributeName = "Background";
        structure.AttributeValue = "CornflowerBlue";
        await structure.SetAttributeCommand.Execute().ToTask();
        await structure.ApplyCommand.Execute().ToTask();
        Assert.Null(structure.Error);
        XDocument saved = XDocument.Parse(new XamlSerializationService().Serialize(document.SyncEngine.CurrentDocument));
        Assert.Equal("Caption", (string?)Assert.Single(saved.Descendants(), element => element.Name.LocalName == "Binding").Attribute("Path"));
        Assert.Equal(ownerName + ".Template", Assert.Single(saved.Descendants(), element => element.Name.LocalName.EndsWith(".Template", StringComparison.Ordinal)).Name.LocalName);
        Assert.Contains(saved.DescendantNodes().OfType<XComment>(), comment => comment.Value == "template note");
        RuntimeDesignRenderResult rendered = new RuntimeDesignRenderer(metadata).Render(document.SyncEngine.CurrentDocument,
            new Uri(path), typeof(PropertyFixtureControl).Assembly);
        Assert.Empty(rendered.Diagnostics);
        PropertyFixtureControl control = Assert.IsType<PropertyFixtureControl>(rendered.Root);
        control.ApplyTemplate();
        Border border = Assert.Single(control.GetVisualChildren().OfType<Border>());
        Assert.Equal(Colors.CornflowerBlue, Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
    }

    private static async Task FlushAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}
