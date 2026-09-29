using System;
using System.IO;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.PropertyEditorExtension;
using XamlVisualEditor.PropertyEditorExtension.Views;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Serialization;
using EditorFeatures = XamlVisualEditor.TestControls.EditorFeatures;
using PropertyFixtureControl = XamlVisualEditor.TestControls.PropertyFixtureControl;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PropertyEditingWorkflowTests
{
    [AvaloniaTheory(Timeout = 30000)]
    [InlineData("library:PropertyFixtureControl", false)]
    [InlineData("CheckBox", false)]
    [InlineData("library:PropertyFixtureControl", true)]
    [InlineData("CheckBox", true)]
    public async Task Inspector_edits_empty_reset_nullable_flags_and_invalid_drafts_and_tracks_undo(string propertyOwner, bool externalPreviewer)
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
        using DesignerDocumentViewModel document = new(path, metadata) { UseExternalPreviewer = externalPreviewer };
        await document.LoadAsync();
        using MainWindowViewModel main = new(metadataService: metadata);
        main.Documents.Add(document);
        main.ActiveDocument = document;
        if (externalPreviewer)
        {
            MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
            DesignItem remote = new(root) { HostInstanceId = 1, HostBounds = new Avalonia.Rect(0, 0, 200, 100) };
            document.DesignSurface.SetDesignTree(remote,
                new System.Collections.Generic.Dictionary<Guid, DesignItem> { [root.Id] = remote },
                new System.Collections.Generic.Dictionary<Control, DesignItem>());
        }
        document.SetSelectedNode(document.SyncEngine.CurrentDocument!.Root!.Id, SyncSource.TreeView);
        using ShellDesignerHost host = new(main);
        InMemorySettingsStore settings = new();
        using PropertyEditorPanelViewModel inspector = new(host, new PropertyEditorRegistry(), settings);
        await inspector.InitializeAsync(CancellationToken.None);
        Assert.True(inspector.CollapseOtherCategoriesOnSelection);
        inspector.CollapseOtherCategoriesOnSelection = false;
        Assert.False(settings.Get<bool>("propertyEditor.collapseOtherCategories"));
        inspector.CollapseOtherCategoriesOnSelection = true;
        Assert.True(settings.Get<bool>("propertyEditor.collapseOtherCategories"));
        PropertyRowViewModel[] grouped = inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>().ToArray();
        Assert.Equal(UiStrings.EssentialProperties, grouped[0].GroupName);
        Assert.Contains(grouped, row => row.Property is { Name: "Width", IsSet: false }
            && row.GroupName == UiStrings.EssentialProperties);
        Assert.Contains(grouped, row => row.Property is { Name: "Padding", IsSet: true }
            && row.GroupName == UiStrings.EssentialProperties);
        Assert.Contains(grouped, row => row.Property is { Name: "Features" } && row.GroupName == UiStrings.LocalValues);
        Assert.All(grouped.Where(row => row.Property is { IsSet: false }),
            row => Assert.NotEqual(UiStrings.LocalValues, row.GroupName));
        Assert.Contains(grouped, row => row.IsHeader && row.CanCollapse);
        PropertyRowViewModel categoryHeader = grouped.First(row => row.IsHeader && row.CanCollapse);
        categoryHeader.IsExpanded = true;
        Assert.Same(categoryHeader, inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>()
            .First(row => row.IsHeader && row.GroupName == categoryHeader.GroupName));
        Assert.Contains(inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>(),
            row => row.Property is not null && row.GroupName == categoryHeader.GroupName);
        categoryHeader.IsExpanded = false;
        Assert.Same(categoryHeader, inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>()
            .First(row => row.IsHeader && row.GroupName == categoryHeader.GroupName));
        Assert.DoesNotContain(inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>(),
            row => row.Property is not null && row.GroupName == categoryHeader.GroupName);
        await inspector.ExpandAllCategoriesCommand.Execute().ToTask();
        Assert.All(inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>()
            .Where(row => row.IsHeader && row.CanCollapse), row => Assert.True(row.IsExpanded));
        Assert.Same(categoryHeader, inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>()
            .First(row => row.IsHeader && row.GroupName == categoryHeader.GroupName));
        await inspector.CollapseAllCategoriesCommand.Execute().ToTask();
        Assert.All(inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>()
            .Where(row => row.IsHeader && row.CanCollapse), row => Assert.False(row.IsExpanded));
        Assert.All(inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>()
            .Where(row => row.IsPinned), row => Assert.True(row.IsExpanded));
        PropertyCategoryOption layout = Assert.Single(inspector.Categories, option => option.Value == "Layout");
        inspector.SelectedCategory = layout;
        Assert.NotEmpty(inspector.PropertiesView.Cast<PropertyEntryViewModel>());
        Assert.All(inspector.PropertiesView.Cast<PropertyEntryViewModel>(), entry => Assert.Equal("Layout", entry.Category));
        Assert.All(inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>(),
            row => Assert.True(row.Property is null || row.Property.Category == "Layout"));
        inspector.SelectedCategory = inspector.Categories[0];
        inspector.ShowLocalValuesOnly = true;
        Assert.Contains(inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>(),
            row => row.Property is { Name: "Width", IsSet: false } && row.GroupName == UiStrings.EssentialProperties);
        inspector.ShowLocalValuesOnly = false;
        if (propertyOwner == "CheckBox" && !externalPreviewer)
        {
            FluentTheme theme = new();
            Avalonia.Application.Current!.Styles.Add(theme);
            PropertyEditorPanelView propertyView = new() { DataContext = inspector };
            IDataTemplate valueTemplate = Assert.IsAssignableFrom<IDataTemplate>(propertyView.Resources["PropertyValueTemplate"]);
            PropertyEntryViewModel paddingEntry = Entry("Padding");
            Assert.True(paddingEntry.IsSet);
            Control valueRoot = Assert.IsAssignableFrom<Control>(valueTemplate.Build(paddingEntry));
            valueRoot.DataContext = paddingEntry;
            propertyView.Content = valueRoot;
            Window propertyWindow = new() { Width = 480, Height = 700,
                Content = propertyView };
            try
            {
                propertyWindow.Show();
                Dispatcher.UIThread.RunJobs();
                propertyWindow.UpdateLayout();
                ContentControl changedValue = Assert.Single(propertyView.GetVisualDescendants().OfType<ContentControl>(),
                    control => control.Classes.Contains("local-value"));
                Assert.Equal(FontWeight.SemiBold, changedValue.FontWeight);
            }
            finally
            {
                propertyWindow.Close();
                Avalonia.Application.Current.Styles.Remove(theme);
            }
        }
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
        if (externalPreviewer)
        {
            Assert.Empty(document.DesignSurface.ControlMap);
            Assert.Null(document.DesignSurface.RootItem?.VisualElement);
            Assert.Equal("CornflowerBlue", (string?)Assert.Single(saved.Descendants(), element => element.Name.LocalName == "Border").Attribute("Background"));
            document.DesignSurface.AutoGenerateAutomationIds = true;
            string parentId = document.SyncEngine.CurrentDocument.Root.Id.ToString("D");
            string? insertedId = await host.InsertElementAsync("Button", "https://github.com/avaloniaui", parentId, CancellationToken.None);
            Assert.NotNull(insertedId);
            MutableAstObjectNode inserted = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(Guid.Parse(insertedId)));
            Assert.Equal("Button_1", inserted.GetPropertyValue("AutomationProperties.AutomationId"));
            string? wrapperId = await host.WrapNodeAsync(insertedId, "Grid", "https://github.com/avaloniaui", CancellationToken.None);
            Assert.NotNull(wrapperId);
            MutableAstObjectNode wrapper = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(Guid.Parse(wrapperId)));
            Assert.Equal("Grid_1", wrapper.GetPropertyValue("AutomationProperties.AutomationId"));
            Assert.Equal("Button_1", inserted.GetPropertyValue("AutomationProperties.AutomationId"));
            document.SyncEngine.Undo();
            Assert.Null(document.NodeMap.FindById(Guid.Parse(wrapperId)));
            Assert.NotNull(document.NodeMap.FindById(Guid.Parse(insertedId)));
            Assert.Empty(document.DesignSurface.ControlMap);
            return;
        }
        RuntimeDesignRenderResult rendered = new RuntimeDesignRenderer(metadata).Render(document.SyncEngine.CurrentDocument,
            new Uri(path), typeof(PropertyFixtureControl).Assembly);
        Assert.Empty(rendered.Diagnostics);
        PropertyFixtureControl control = Assert.IsType<PropertyFixtureControl>(rendered.Root);
        control.ApplyTemplate();
        Border border = Assert.Single(control.GetVisualChildren().OfType<Border>());
        Assert.Equal(Colors.CornflowerBlue, Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_applies_an_edit_to_every_selected_node_in_one_undo_step()
    {
        string xaml = """
            <StackPanel xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Label x:Name="caption" VerticalAlignment="Center" Width="10" />
              <TextBox x:Name="input" VerticalAlignment="Center" />
            </StackPanel>
            """;
        string path = Path.Combine(Path.GetTempPath(), "xve-multi-properties-" + Guid.NewGuid().ToString("N") + ".axaml");
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, xaml);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        using DesignerDocumentViewModel document = new(path, metadata);
        await document.LoadAsync();
        using MainWindowViewModel main = new(metadataService: metadata);
        main.Documents.Add(document);
        main.ActiveDocument = document;
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode label = Assert.Single(root.Children.OfType<MutableAstObjectNode>(), node => node.TypeName == "Label");
        MutableAstObjectNode textBox = Assert.Single(root.Children.OfType<MutableAstObjectNode>(), node => node.TypeName == "TextBox");
        DesignItem rootItem = new(root);
        DesignItem labelItem = new(label);
        DesignItem textBoxItem = new(textBox);
        document.DesignSurface.SetDesignTree(rootItem,
            new System.Collections.Generic.Dictionary<Guid, DesignItem>
            {
                [root.Id] = rootItem, [label.Id] = labelItem, [textBox.Id] = textBoxItem
            },
            new System.Collections.Generic.Dictionary<Control, DesignItem>());
        document.DesignSurface.Selection.SetSelection([labelItem, textBoxItem]);
        using ShellDesignerHost host = new(main);
        using PropertyEditorPanelViewModel inspector = new(host, new PropertyEditorRegistry(), new InMemorySettingsStore());
        await inspector.InitializeAsync(CancellationToken.None);
        PropertyEntryViewModel[] entries = inspector.PropertiesView.Cast<PropertyEntryViewModel>().ToArray();
        PropertyEntryViewModel Entry(string name) => Assert.Single(entries, entry => entry.Name == name);

        Assert.Equal("Label, TextBox (2)", inspector.SelectedTypeName);
        Assert.DoesNotContain(entries, entry => entry.Name is "Name" or "Text" or "Target");
        Assert.Empty(inspector.EventsView.Cast<EventEntryViewModel>());
        Assert.False(Entry("Width").IsSet);
        PropertyEntryViewModel alignment = Entry("VerticalAlignment");
        Assert.Equal("Center", alignment.Value);

        alignment.Value = "Top";
        await alignment.CommitCommand.Execute().ToTask();
        Assert.Null(inspector.StatusText);
        Assert.Equal("Top", label.GetPropertyValue("VerticalAlignment"));
        Assert.Equal("Top", textBox.GetPropertyValue("VerticalAlignment"));

        document.SyncEngine.Undo();
        MutableAstObjectNode[] restored = document.SyncEngine.CurrentDocument!.Root!.Children.OfType<MutableAstObjectNode>()
            .Where(node => node.TypeName is "Label" or "TextBox").ToArray();
        Assert.Equal(2, restored.Length);
        Assert.All(restored, node => Assert.Equal("Center", node.GetPropertyValue("VerticalAlignment")));
    }

    [AvaloniaFact]
    public async Task ExternalPreviewSuppressesInactiveLocalRenderErrorsButKeepsSyntaxAndHostErrors()
    {
        using DesignerDocumentViewModel document = new("RendererDiagnostics.axaml");
        await document.SyncEngine.LoadAsync("<UserControl xmlns=\"https://github.com/avaloniaui\" />");
        await FlushAsync();
        document.SetRuntimeRenderFailure(new InvalidOperationException("Local renderer failed."));
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Source == "Designer");
        document.UseExternalPreviewer = true;
        await FlushAsync();
        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Source == "Designer");
        using PreviewerTcpSession session = new(document.FilePath, log: null);
        document.PreviewerSession = session;
        session.ReportError("External renderer failed.");
        await FlushAsync();
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Source == "Previewer");
        document.UseExternalPreviewer = false;
        await FlushAsync();
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Source == "Designer");
        document.UseExternalPreviewer = true;
        document.SyncEngine.NotifyTextChanged("<UserControl", SyncSource.CodeEditor);
        await document.SyncEngine.SaveAsync();
        await FlushAsync();
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Source == "XAML");
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Source == "Previewer");
        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Source == "Designer");
    }

    [AvaloniaFact]
    public async Task RejectedGestureKeepsItsDiagnosticAfterRollbackAndClearsItAfterAnEdit()
    {
        const string xaml = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button Canvas.Left=\"10\" Canvas.Top=\"{Binding Position}\" /></Canvas>";
        using DesignerDocumentViewModel document = new("GestureDiagnostics.axaml") { UseExternalPreviewer = true };
        await document.SyncEngine.LoadAsync(xaml);
        await FlushAsync();
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode button = Assert.IsType<MutableAstObjectNode>(Assert.Single(root.Children));
        document.SetSelectedNode(button.Id, SyncSource.DesignSurface);
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 200, 100, new[]
        {
            new DesignerHostNode(root.Id, 1, null, "Canvas", null, 0, 0, 200, 100, true, Layout: new() { Kind = "Canvas" }),
            new DesignerHostNode(button.Id, 2, 1, "Button", null, 10, 20, 80, 30, true, Layout: new() { CanvasLeft = 10, CanvasTop = 20 })
        });
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PreviewerGeometryEditor(document, tree).Nudge(new[] { new DesignItem(button) { HostInstanceId = 2 } }, 1, 1));
        document.SetDesignerInteractionError(error);
        await FlushAsync();
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "XVE_EDIT" && diagnostic.Message.Contains("Canvas.Top", StringComparison.Ordinal));
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        Assert.Equal(0, document.SyncEngine.UndoRedo.UndoCount);
        button = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(button.Id));
        using (DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Change content"))
        {
            button.SetPropertyValue("Content", "Updated");
            edit.Commit();
        }
        await FlushAsync();
        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Code == "XVE_EDIT");
    }

    private static async Task FlushAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}
