using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
    private const string PropertyFixtureOwner = "library:PropertyFixtureControl";
    private const string CollapseCategoriesSetting = "propertyEditor.collapseOtherCategories";
    private const string LabelAndTextBoxXaml = """
        <StackPanel xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Label x:Name="caption" VerticalAlignment="Center" Width="10" />
          <TextBox x:Name="input" VerticalAlignment="Center" />
        </StackPanel>
        """;

    [AvaloniaTheory(Timeout = 30000)]
    [InlineData(PropertyFixtureOwner)]
    [InlineData("CheckBox")]
    public async Task Inspector_groups_essential_properties_before_local_values(string propertyOwner)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(FixtureXaml(propertyOwner), false, cancellationToken);
        PropertyRowViewModel[] grouped = session.GroupedRows();
        Assert.Equal(UiStrings.EssentialProperties, grouped[0].GroupName);
        Assert.Contains(grouped, row => row.Property is { Name: "Width", IsSet: false }
            && row.GroupName == UiStrings.EssentialProperties);
        Assert.Contains(grouped, row => row.Property is { Name: "Padding", IsSet: true }
            && row.GroupName == UiStrings.EssentialProperties);
        Assert.Contains(grouped, row => row.Property is { Name: "Features" } && row.GroupName == UiStrings.LocalValues);
        Assert.All(grouped.Where(row => row.Property is { IsSet: false }),
            row => Assert.NotEqual(UiStrings.LocalValues, row.GroupName));
        Assert.Contains(grouped, row => row.IsHeader && row.CanCollapse);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_toggles_one_category_and_keeps_its_header()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(FixtureXaml("CheckBox"), false, cancellationToken);
        PropertyRowViewModel categoryHeader = session.GroupedRows().First(row => row.IsHeader && row.CanCollapse);
        Assert.False(categoryHeader.IsExpanded);
        categoryHeader.IsExpanded = true;
        Assert.Same(categoryHeader, session.GroupedRows().First(row => row.IsHeader && row.GroupName == categoryHeader.GroupName));
        Assert.Contains(session.GroupedRows(), row => row.Property is not null && row.GroupName == categoryHeader.GroupName);
        categoryHeader.IsExpanded = false;
        Assert.Same(categoryHeader, session.GroupedRows().First(row => row.IsHeader && row.GroupName == categoryHeader.GroupName));
        Assert.DoesNotContain(session.GroupedRows(), row => row.Property is not null && row.GroupName == categoryHeader.GroupName);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_expands_and_collapses_all_categories_with_one_rebuild()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(FixtureXaml("CheckBox"), false, cancellationToken);
        PropertyEditorPanelViewModel inspector = session.Inspector;
        PropertyRowViewModel[] headers = CollapsibleHeaders(inspector);
        Assert.True(headers.Length > 1);
        Assert.All(headers, header => Assert.False(header.IsExpanded));
        List<NotifyCollectionChangedAction> changes = [];
        NotifyCollectionChangedEventHandler recordChange = (_, args) => changes.Add(args.Action);
        INotifyCollectionChanged rowsView = inspector.GroupedPropertiesView;
        rowsView.CollectionChanged += recordChange;
        try
        {
            await inspector.ExpandAllCategoriesCommand.Execute().ToTask(cancellationToken);
        }
        finally
        {
            rowsView.CollectionChanged -= recordChange;
        }

        Assert.InRange(changes.Count, 1, 2);
        Assert.All(changes, action => Assert.Equal(NotifyCollectionChangedAction.Reset, action));
        headers = CollapsibleHeaders(inspector);
        Assert.All(headers, header => Assert.True(header.IsExpanded));
        PropertyRowViewModel[] rows = session.GroupedRows();
        Assert.All(headers, header => Assert.Contains(rows, row => row.Property is not null && row.GroupName == header.GroupName));

        await inspector.CollapseAllCategoriesCommand.Execute().ToTask(cancellationToken);
        rows = session.GroupedRows();
        Assert.All(CollapsibleHeaders(inspector), header => Assert.False(header.IsExpanded));
        Assert.All(rows.Where(row => row.IsPinned), row => Assert.True(row.IsExpanded));
        Assert.DoesNotContain(rows, row => row.Property is not null
            && headers.Any(header => string.Equals(header.GroupName, row.GroupName, StringComparison.Ordinal)));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_limits_the_rows_to_the_category_filter_and_to_local_values()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(FixtureXaml("CheckBox"), false, cancellationToken);
        PropertyEditorPanelViewModel inspector = session.Inspector;
        PropertyCategoryOption layout = Assert.Single(inspector.Categories, option => option.Value == "Layout");
        inspector.SelectedCategory = layout;
        Assert.NotEmpty(inspector.PropertiesView.Cast<PropertyEntryViewModel>());
        Assert.All(inspector.PropertiesView.Cast<PropertyEntryViewModel>(), entry => Assert.Equal("Layout", entry.Category));
        Assert.All(session.GroupedRows(), row => Assert.True(row.Property is null || row.Property.Category == "Layout"));
        inspector.SelectedCategory = inspector.Categories[0];
        inspector.ShowLocalValuesOnly = true;
        Assert.Contains(session.GroupedRows(),
            row => row.Property is { Name: "Width", IsSet: false } && row.GroupName == UiStrings.EssentialProperties);
        Assert.DoesNotContain(session.GroupedRows(), row => row.IsHeader && row.CanCollapse);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_keeps_the_category_filter_when_another_control_is_selected()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(LabelAndTextBoxXaml, false, cancellationToken,
            selectedChildren: [0]);
        PropertyEditorPanelViewModel inspector = session.Inspector;
        Assert.Equal("Label", inspector.SelectedTypeName);
        inspector.SelectedCategory = Assert.Single(inspector.Categories, option => option.Value == "Layout");

        await session.SelectAsync(cancellationToken, 1);
        Assert.Equal("TextBox", inspector.SelectedTypeName);
        Assert.Same(Assert.Single(inspector.Categories, option => option.Value == "Layout"), inspector.SelectedCategory);
        PropertyEntryViewModel[] visible = inspector.PropertiesView.Cast<PropertyEntryViewModel>().ToArray();
        Assert.NotEmpty(visible);
        Assert.All(visible, entry => Assert.Equal("Layout", entry.Category));
        Assert.All(session.GroupedRows(), row => Assert.True(row.Property is null || row.Property.Category == "Layout"));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_marks_a_local_value_in_the_value_template()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(FixtureXaml("CheckBox"), false, cancellationToken);
        FluentTheme theme = new();
        Avalonia.Application.Current!.Styles.Add(theme);
        PropertyEditorPanelView propertyView = new() { DataContext = session.Inspector };
        IDataTemplate valueTemplate = Assert.IsAssignableFrom<IDataTemplate>(propertyView.Resources["PropertyValueTemplate"]);
        PropertyEntryViewModel paddingEntry = session.Entry("Padding");
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

    [AvaloniaTheory(Timeout = 30000)]
    [InlineData(PropertyFixtureOwner)]
    [InlineData("CheckBox")]
    public async Task Inspector_writes_empty_content_as_a_local_value_and_reset_removes_it(string propertyOwner)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(FixtureXaml(propertyOwner), false, cancellationToken);
        string ownerName = OwnerName(propertyOwner);
        PropertyEntryViewModel content = session.Entry("Content");
        Assert.Equal("seed", content.Value);
        Assert.True(content.AcceptsEmptyText);
        Assert.DoesNotContain(session.Inspector.PropertiesView.Cast<PropertyEntryViewModel>(), entry => entry.Name == ownerName + ".Content");
        content.Value = string.Empty;
        await content.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.Equal(string.Empty, session.Root.GetPropertyValue(ownerName + ".Content"));
        Assert.True(content.IsSet);
        await content.ResetCommand.Execute().ToTask(cancellationToken);
        Assert.Null(session.Root.FindProperty(ownerName + ".Content"));
        Assert.False(content.IsSet);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_removes_a_value_type_assignment_when_its_field_is_emptied()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(FixtureXaml("CheckBox"), false, cancellationToken);
        int undoCount = session.Document.SyncEngine.UndoRedo.UndoCount;
        PropertyEntryViewModel padding = session.Entry("Padding");
        Assert.False(padding.AcceptsEmptyText);
        padding.Value = string.Empty;
        await padding.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.Null(session.Root.FindProperty("Padding"));
        Assert.Null(padding.Value);
        Assert.False(padding.IsSet);
        Assert.False(padding.HasValidationError);
        Assert.Equal(undoCount + 1, session.Document.SyncEngine.UndoRedo.UndoCount);

        PropertyEntryViewModel width = session.Entry("Width");
        Assert.False(width.IsSet);
        width.Value = " ";
        await width.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.Null(session.Root.FindProperty("Width"));
        Assert.Null(width.Value);
        Assert.False(width.IsSet);
        Assert.Equal(undoCount + 1, session.Document.SyncEngine.UndoRedo.UndoCount);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_writes_nullable_boolean_and_flag_values()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(FixtureXaml(PropertyFixtureOwner), false, cancellationToken);
        PropertyEntryViewModel nullable = session.Entry("IsChecked");
        Assert.Equal("{x:Null}", nullable.Value);
        nullable.BoolValue = true;
        await FlushAsync().WaitAsync(cancellationToken);
        Assert.Equal("True", session.Root.GetPropertyValue("IsChecked"));
        PropertyEntryViewModel flags = session.Entry("Features");
        Assert.True(flags.IsFlagsEnum);
        Assert.Single(flags.FlagOptions, option => option.Name == "High").IsChecked = true;
        await FlushAsync().WaitAsync(cancellationToken);
        Assert.Equal(EditorFeatures.High | EditorFeatures.Alpha,
            Enum.Parse<EditorFeatures>(session.Root.GetPropertyValue("Features")!));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_keeps_an_invalid_draft_and_follows_undo_and_redo()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(FixtureXaml(PropertyFixtureOwner), false, cancellationToken);
        PropertyEntryViewModel padding = session.Entry("Padding");
        padding.Value = "invalid thickness";
        await padding.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.True(padding.HasValidationError);
        Assert.Equal("invalid thickness", padding.Value);
        Assert.Equal("1,2,3,4", session.Root.GetPropertyValue("Padding"));
        padding.Value = "8,9";
        await padding.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.False(padding.HasValidationError);
        Assert.Null(session.Inspector.StatusText);
        Assert.Equal("8,9", session.Root.GetPropertyValue("Padding"));

        session.Document.SyncEngine.Undo();
        await FlushAsync().WaitAsync(cancellationToken);
        Assert.Equal("1,2,3,4", session.Root.GetPropertyValue("Padding"));
        Assert.Equal("1,2,3,4", session.Entry("Padding").Value);
        session.Document.SyncEngine.Redo();
        await FlushAsync().WaitAsync(cancellationToken);
        Assert.Equal("8,9", session.Entry("Padding").Value);
    }

    [AvaloniaTheory(Timeout = 30000)]
    [InlineData(PropertyFixtureOwner)]
    [InlineData("CheckBox")]
    public async Task Inspector_resets_a_property_element_and_undo_restores_it(string propertyOwner)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(FixtureXaml(propertyOwner), false, cancellationToken);
        PropertyEntryViewModel tag = session.Entry("Tag");
        Assert.True(tag.IsSet);
        await tag.ResetCommand.Execute().ToTask(cancellationToken);
        Assert.Null(session.Root.FindProperty(OwnerName(propertyOwner) + ".Tag"));
        session.Document.SyncEngine.Undo();
        await FlushAsync().WaitAsync(cancellationToken);
        Assert.True(session.Entry("Tag").IsSet);
    }

    [AvaloniaTheory(Timeout = 30000)]
    [InlineData(PropertyFixtureOwner, false)]
    [InlineData("CheckBox", false)]
    [InlineData(PropertyFixtureOwner, true)]
    [InlineData("CheckBox", true)]
    public async Task Structure_editor_changes_a_template_and_keeps_bindings_and_comments(string propertyOwner, bool externalPreviewer)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(FixtureXaml(propertyOwner), externalPreviewer, cancellationToken);
        string ownerName = OwnerName(propertyOwner);
        PropertyEntryViewModel template = session.Entry("Template");
        Assert.Equal(PropertyEditorKind.Template, template.EditorKind);
        Assert.True(template.HasStructureEditor);
        DesignerPropertyStructure snapshot = await session.Host.OpenPropertyStructureAsync(
            session.Root.Id.ToString("D"), "Template", cancellationToken);
        using PropertyStructureEditorViewModel structure = new(session.Host, snapshot);
        structure.SelectedNode = Assert.Single(structure.Roots.SelectMany(node => node.DescendantsAndSelf()), node => node.Label == "Border");
        structure.AttributeName = "Background";
        structure.AttributeValue = "CornflowerBlue";
        await structure.SetAttributeCommand.Execute().ToTask(cancellationToken);
        await structure.ApplyCommand.Execute().ToTask(cancellationToken);
        Assert.Null(structure.Error);
        MutableAstDocument current = session.Document.SyncEngine.CurrentDocument!;
        XDocument saved = XDocument.Parse(new XamlSerializationService().Serialize(current));
        Assert.Equal("Caption", (string?)Assert.Single(saved.Descendants(), element => element.Name.LocalName == "Binding").Attribute("Path"));
        Assert.Equal(ownerName + ".Template", Assert.Single(saved.Descendants(), element => element.Name.LocalName.EndsWith(".Template", StringComparison.Ordinal)).Name.LocalName);
        Assert.Contains(saved.DescendantNodes().OfType<XComment>(), comment => comment.Value == "template note");
        if (externalPreviewer)
        {
            Assert.Empty(session.Document.DesignSurface.ControlMap);
            Assert.Null(session.Document.DesignSurface.RootItem?.VisualElement);
            Assert.Equal("CornflowerBlue", (string?)Assert.Single(saved.Descendants(), element => element.Name.LocalName == "Border").Attribute("Background"));
            return;
        }

        RuntimeDesignRenderResult rendered = new RuntimeDesignRenderer(session.Metadata).Render(current,
            new Uri(session.FilePath), typeof(PropertyFixtureControl).Assembly);
        Assert.Empty(rendered.Diagnostics);
        PropertyFixtureControl control = Assert.IsType<PropertyFixtureControl>(rendered.Root);
        control.ApplyTemplate();
        Border border = Assert.Single(control.GetVisualChildren().OfType<Border>());
        Assert.Equal(Colors.CornflowerBlue, Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_applies_an_edit_to_every_selected_node_in_one_undo_step()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(LabelAndTextBoxXaml, false, cancellationToken,
            selectedChildren: [0, 1]);
        PropertyEditorPanelViewModel inspector = session.Inspector;
        MutableAstObjectNode label = Assert.Single(session.ChildNodes, node => node.TypeName == "Label");
        MutableAstObjectNode textBox = Assert.Single(session.ChildNodes, node => node.TypeName == "TextBox");
        PropertyEntryViewModel[] entries = inspector.PropertiesView.Cast<PropertyEntryViewModel>().ToArray();
        PropertyEntryViewModel Entry(string name) => Assert.Single(entries, entry => entry.Name == name);

        Assert.Equal("Label, TextBox (2)", inspector.SelectedTypeName);
        Assert.DoesNotContain(entries, entry => entry.Name is "Name" or "Text" or "Target");
        Assert.Empty(inspector.EventsView.Cast<EventEntryViewModel>());
        Assert.False(Entry("Height").IsSet);
        Assert.False(Entry("Height").IsMixed);
        Assert.True(Entry("Width").IsMixed);
        Assert.True(Entry("Width").IsSet);
        PropertyEntryViewModel alignment = Entry("VerticalAlignment");
        Assert.Equal("Center", alignment.Value);
        Assert.False(alignment.IsMixed);

        alignment.Value = "Top";
        await alignment.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.Null(inspector.StatusText);
        Assert.Equal("Top", label.GetPropertyValue("VerticalAlignment"));
        Assert.Equal("Top", textBox.GetPropertyValue("VerticalAlignment"));

        session.Document.SyncEngine.Undo();
        MutableAstObjectNode[] restored = session.ChildNodes.Where(node => node.TypeName is "Label" or "TextBox").ToArray();
        Assert.Equal(2, restored.Length);
        Assert.All(restored, node => Assert.Equal("Center", node.GetPropertyValue("VerticalAlignment")));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_shows_differing_values_as_mixed_local_values_that_reset_removes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string xaml = """
            <StackPanel xmlns="https://github.com/avaloniaui">
              <Button Background="Red" />
              <Button Background="Blue" />
              <Button />
            </StackPanel>
            """;
        using InspectorSession session = await InspectorSession.CreateAsync(xaml, false, cancellationToken,
            selectedChildren: [0, 1, 2]);
        PropertyEditorPanelViewModel inspector = session.Inspector;
        PropertyEntryViewModel background = session.Entry("Background");
        Assert.True(background.IsMixed);
        Assert.True(background.IsSet);
        Assert.Null(background.Value);
        Assert.Equal(UiStrings.ValueSourceMixed, background.ValueSourceDescription);
        Assert.Contains(session.GroupedRows(), row => ReferenceEquals(row.Property, background) && row.GroupName == UiStrings.LocalValues);
        inspector.ShowLocalValuesOnly = true;
        Assert.Contains(inspector.PropertiesView.Cast<PropertyEntryViewModel>(), entry => ReferenceEquals(entry, background));
        Assert.Contains(session.GroupedRows(), row => ReferenceEquals(row.Property, background));
        inspector.ShowLocalValuesOnly = false;

        int undoCount = session.Document.SyncEngine.UndoRedo.UndoCount;
        await background.ResetCommand.Execute().ToTask(cancellationToken);
        Assert.Null(inspector.StatusText);
        Assert.All(session.ChildNodes, node => Assert.Null(node.FindProperty("Background")));
        Assert.False(background.IsSet);
        Assert.False(background.IsMixed);
        Assert.Equal(undoCount + 1, session.Document.SyncEngine.UndoRedo.UndoCount);

        session.Document.SyncEngine.Undo();
        Assert.Equal(new string?[] { "Red", "Blue", null },
            session.ChildNodes.Select(node => node.GetPropertyValue("Background")).ToArray());
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Inspector_rolls_back_every_node_when_the_edit_fails_on_a_later_node()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using InspectorSession session = await InspectorSession.CreateAsync(LabelAndTextBoxXaml, false, cancellationToken,
            selectedChildren: [0, 1],
            wrapHost: created => new FailingNodeDesignerHost(created.Host, created.ChildNodes[1].Id.ToString("D")));
        PropertyEditorPanelViewModel inspector = session.Inspector;
        int undoCount = session.Document.SyncEngine.UndoRedo.UndoCount;
        PropertyEntryViewModel alignment = session.Entry("VerticalAlignment");

        alignment.Value = "Top";
        await alignment.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.Equal(UiStrings.PropertyValueApplyFailed, alignment.ValidationError);
        Assert.Equal("Top", alignment.Value);
        Assert.Equal($"VerticalAlignment: {UiStrings.PropertyValueApplyFailed}", inspector.StatusText);
        Assert.All(session.ChildNodes, node => Assert.Equal("Center", node.GetPropertyValue("VerticalAlignment")));
        Assert.Equal(undoCount, session.Document.SyncEngine.UndoRedo.UndoCount);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Collapse_preference_defaults_to_collapsed_categories_and_is_stored_for_the_user()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ScriptedDesignerHost host = new() { SelectedNodes = [Node("first")], Properties = CategorizedProperties() };
        InMemorySettingsStore settings = new();
        using PropertyEditorPanelViewModel inspector = await CreateInspectorAsync(host, settings, cancellationToken);
        Assert.True(inspector.CollapseOtherCategoriesOnSelection);
        Assert.Equal(2, CollapsibleHeaders(inspector).Length);
        Assert.All(CollapsibleHeaders(inspector), header => Assert.False(header.IsExpanded));

        inspector.CollapseOtherCategoriesOnSelection = false;
        Assert.False(settings.Get(CollapseCategoriesSetting, true));
        Assert.All(CollapsibleHeaders(inspector), header => Assert.True(header.IsExpanded));
        inspector.CollapseOtherCategoriesOnSelection = true;
        Assert.True(settings.Get(CollapseCategoriesSetting, false));
        Assert.All(CollapsibleHeaders(inspector), header => Assert.False(header.IsExpanded));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Stored_collapse_preference_is_read_by_a_new_panel()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ScriptedDesignerHost host = new() { SelectedNodes = [Node("first")], Properties = CategorizedProperties() };
        InMemorySettingsStore settings = new();
        await settings.UpdateAsync(CollapseCategoriesSetting, false, SettingsTarget.User, cancellationToken);
        using PropertyEditorPanelViewModel inspector = await CreateInspectorAsync(host, settings, cancellationToken);
        Assert.False(inspector.CollapseOtherCategoriesOnSelection);
        PropertyRowViewModel[] headers = CollapsibleHeaders(inspector);
        Assert.Equal(2, headers.Length);
        Assert.All(headers, header => Assert.True(header.IsExpanded));
        Assert.Contains(inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>(), row => row.Property is { Name: "Opacity" });
        Assert.Contains(inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>(), row => row.Property is { Name: "Tag" });
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Event_commit_assigns_the_trimmed_handler_and_is_applying_until_the_host_answers()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ScriptedDesignerHost host = new() { SelectedNodes = [Node("first")] };
        using PropertyEditorPanelViewModel inspector = await CreateInspectorAsync(host, null, cancellationToken);
        EventEntryViewModel click = Assert.Single(inspector.EventsView.Cast<EventEntryViewModel>());
        TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.PendingHandlerResult = answer;

        click.HandlerName = " OnFirstClick ";
        await click.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.True(click.IsApplying);
        Assert.Equal("first.Click=OnFirstClick", Assert.Single(host.HandlerRequests));
        Assert.False(host.Handlers.ContainsKey("first"));

        answer.SetResult(true);
        await WaitUntilAsync(() => !click.IsApplying, cancellationToken);
        Assert.Equal("OnFirstClick", host.Handlers["first"]);
        Assert.Equal("OnFirstClick", click.CommittedHandler);
        Assert.Equal("OnFirstClick", click.HandlerName);
        Assert.False(click.HasValidationError);
        Assert.Null(inspector.StatusText);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Event_commit_survives_a_selection_change_while_the_host_resolves_the_handler()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ScriptedDesignerHost host = new() { SelectedNodes = [Node("first")] };
        using PropertyEditorPanelViewModel inspector = await CreateInspectorAsync(host, null, cancellationToken);
        EventEntryViewModel first = Assert.Single(inspector.EventsView.Cast<EventEntryViewModel>());
        TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.PendingHandlerResult = answer;
        first.HandlerName = "OnFirstClick";
        await first.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.True(first.IsApplying);

        host.SelectedNodes = [Node("second")];
        await inspector.HandleSelectionChangedAsync(host.SelectedNodes, cancellationToken);
        Assert.Equal("second", Assert.Single(inspector.EventsView.Cast<EventEntryViewModel>()).NodeId);

        answer.SetResult(true);
        await WaitUntilAsync(() => !first.IsApplying, cancellationToken);
        Assert.True(host.Handlers.TryGetValue("first", out string? assigned));
        Assert.Equal("OnFirstClick", assigned);
        Assert.Equal("OnFirstClick", first.CommittedHandler);
        Assert.False(first.HasValidationError);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Event_cancel_restores_the_committed_handler_and_sends_nothing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ScriptedDesignerHost host = new() { SelectedNodes = [Node("first")] };
        host.Handlers["first"] = "OnSaved";
        using PropertyEditorPanelViewModel inspector = await CreateInspectorAsync(host, null, cancellationToken);
        EventEntryViewModel click = Assert.Single(inspector.EventsView.Cast<EventEntryViewModel>());
        Assert.Equal("OnSaved", click.HandlerName);

        click.HandlerName = "OnTyped";
        await click.CancelEditCommand.Execute().ToTask(cancellationToken);
        Assert.Equal("OnSaved", click.HandlerName);
        await click.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.Empty(host.HandlerRequests);
        await inspector.UpdateSelectionAsync(host.SelectedNodes, cancellationToken, forceReload: true);
        Assert.Equal("OnSaved", Assert.Single(inspector.EventsView.Cast<EventEntryViewModel>()).HandlerName);
        Assert.Null(inspector.StatusText);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Rejected_event_handler_keeps_its_draft_and_error_across_a_reload()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ScriptedDesignerHost host = new() { SelectedNodes = [Node("first")], AcceptHandlers = false };
        using PropertyEditorPanelViewModel inspector = await CreateInspectorAsync(host, null, cancellationToken);
        EventEntryViewModel click = Assert.Single(inspector.EventsView.Cast<EventEntryViewModel>());
        string expectedStatus = $"Click: {UiStrings.EventHandlerApplyFailed}";

        click.HandlerName = "OnRejected";
        await click.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.False(click.IsApplying);
        Assert.Equal(UiStrings.EventHandlerApplyFailed, click.ValidationError);
        Assert.Equal(expectedStatus, inspector.StatusText);
        Assert.Null(click.CommittedHandler);

        await inspector.UpdateSelectionAsync(host.SelectedNodes, cancellationToken, forceReload: true);
        EventEntryViewModel reloaded = Assert.Single(inspector.EventsView.Cast<EventEntryViewModel>());
        Assert.Equal("OnRejected", reloaded.HandlerName);
        Assert.Equal(UiStrings.EventHandlerApplyFailed, reloaded.ValidationError);
        Assert.Equal(expectedStatus, inspector.StatusText);

        host.AcceptHandlers = true;
        await reloaded.CommitCommand.Execute().ToTask(cancellationToken);
        Assert.Equal("OnRejected", host.Handlers["first"]);
        Assert.False(reloaded.HasValidationError);
        Assert.Null(inspector.StatusText);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Event_navigation_commits_the_draft_first_and_reports_a_missing_definition()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ScriptedDesignerHost host = new() { SelectedNodes = [Node("first")] };
        using PropertyEditorPanelViewModel inspector = await CreateInspectorAsync(host, null, cancellationToken);
        EventEntryViewModel click = Assert.Single(inspector.EventsView.Cast<EventEntryViewModel>());

        click.HandlerName = "OnClick";
        await click.NavigateCommand.Execute().ToTask(cancellationToken);
        Assert.Equal("OnClick", host.Handlers["first"]);
        Assert.Equal("first.Click", Assert.Single(host.Navigations));
        Assert.False(click.HasValidationError);

        host.CanNavigate = false;
        await click.NavigateCommand.Execute().ToTask(cancellationToken);
        Assert.Equal(2, host.Navigations.Count);
        Assert.Single(host.HandlerRequests);
        Assert.Equal(UiStrings.EventDefinitionOpenFailed, click.ValidationError);
        Assert.Equal($"Click: {UiStrings.EventDefinitionOpenFailed}", inspector.StatusText);
        Assert.False(click.IsApplying);
    }

    private static string FixtureXaml(string propertyOwner) => """
        <library:PropertyFixtureControl xmlns="https://github.com/avaloniaui"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
            xmlns:library="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls"
            Features="Alpha" IsThreeState="True" IsChecked="{x:Null}" Padding="1,2,3,4">
          <OWNER.Content>seed</OWNER.Content>
          <OWNER.Tag><Binding Path="Caption" /></OWNER.Tag>
          <!--template note-->
          <OWNER.Template><ControlTemplate><Border Background="Tomato" /></ControlTemplate></OWNER.Template>
        </library:PropertyFixtureControl>
        """.Replace("OWNER", propertyOwner, StringComparison.Ordinal);

    private static string OwnerName(string propertyOwner) => propertyOwner[(propertyOwner.IndexOf(':') + 1)..];

    private static PropertyRowViewModel[] CollapsibleHeaders(PropertyEditorPanelViewModel inspector) =>
        inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>().Where(row => row.IsHeader && row.CanCollapse).ToArray();

    private static DesignerNodeSummary Node(string nodeId) => new(nodeId, "Button", null, null, 0);

    private static IReadOnlyList<DesignerPropertyInfo> CategorizedProperties() =>
    [
        new DesignerPropertyInfo("Opacity", "System.Double", null, false, "Appearance", null, "1", false, null, null),
        new DesignerPropertyInfo("Tag", "System.Object", null, false, "Common", null, null, false, null, null)
    ];

    private static async Task<PropertyEditorPanelViewModel> CreateInspectorAsync(IDesignerHost host, ISettings? settings,
        CancellationToken cancellationToken)
    {
        PropertyEditorPanelViewModel inspector = new(host, new PropertyEditorRegistry(), settings);
        try
        {
            await inspector.InitializeAsync(cancellationToken);
            return inspector;
        }
        catch
        {
            inspector.Dispose();
            throw;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task FlushAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

    /// <summary>A designer document in the temporary folder with a properties panel on the real shell host.</summary>
    private sealed class InspectorSession : IDisposable
    {
        private DesignerDocumentViewModel? _document;
        private MainWindowViewModel? _main;
        private ShellDesignerHost? _host;
        private PropertyEditorPanelViewModel? _inspector;

        private InspectorSession(string filePath, TypeMetadataService metadata)
        {
            FilePath = filePath;
            Metadata = metadata;
        }

        public string FilePath { get; }
        public TypeMetadataService Metadata { get; }
        public DesignerDocumentViewModel Document => _document!;
        public ShellDesignerHost Host => _host!;
        public PropertyEditorPanelViewModel Inspector => _inspector!;
        public MutableAstObjectNode Root => Document.SyncEngine.CurrentDocument!.Root!;
        public MutableAstObjectNode[] ChildNodes => Root.Children.OfType<MutableAstObjectNode>().ToArray();
        public DesignItem[] ChildItems { get; private set; } = [];

        public PropertyEntryViewModel Entry(string name) =>
            Assert.Single(Inspector.PropertiesView.Cast<PropertyEntryViewModel>(), entry => entry.Name == name);

        public PropertyRowViewModel[] GroupedRows() => Inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>().ToArray();

        /// <summary>
        /// Creates the session. Without <paramref name="selectedChildren" /> the root is selected; otherwise the
        /// children of the root get design items and the listed ones are selected.
        /// </summary>
        public static async Task<InspectorSession> CreateAsync(string xaml, bool externalPreviewer,
            CancellationToken cancellationToken, int[]? selectedChildren = null,
            Func<InspectorSession, IDesignerHost>? wrapHost = null)
        {
            string filePath = Path.Combine(Path.GetTempPath(), "xve-properties-" + Guid.NewGuid().ToString("N") + ".axaml");
            TypeMetadataService metadata = new();
            metadata.LoadAssembly(typeof(Control).Assembly.Location);
            metadata.LoadAssembly(typeof(Brush).Assembly.Location);
            metadata.LoadAssembly(typeof(PropertyFixtureControl).Assembly.Location);
            InspectorSession session = new(filePath, metadata);
            try
            {
                Assert.False(File.Exists(filePath));
                await File.WriteAllTextAsync(filePath, xaml, cancellationToken);
                session._document = new DesignerDocumentViewModel(filePath, metadata) { UseExternalPreviewer = externalPreviewer };
                await session._document.LoadAsync().WaitAsync(cancellationToken);
                session._main = new MainWindowViewModel(metadataService: metadata);
                session._main.Documents.Add(session._document);
                session._main.ActiveDocument = session._document;
                if (selectedChildren is null)
                {
                    session.SelectRoot(externalPreviewer);
                }
                else
                {
                    session.CreateChildItems();
                    session.SetSelection(selectedChildren);
                }

                session._host = new ShellDesignerHost(session._main);
                IDesignerHost designer = wrapHost?.Invoke(session) ?? session._host;
                session._inspector = new PropertyEditorPanelViewModel(designer, new PropertyEditorRegistry(), new InMemorySettingsStore());
                await session._inspector.InitializeAsync(cancellationToken);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public async Task SelectAsync(CancellationToken cancellationToken, params int[] children)
        {
            SetSelection(children);
            IReadOnlyList<DesignerNodeSummary> selected = await Host.GetSelectedNodesAsync(cancellationToken);
            await Inspector.HandleSelectionChangedAsync(selected, cancellationToken);
        }

        public void Dispose()
        {
            _inspector?.Dispose();
            _host?.Dispose();
            _main?.Dispose();
            _document?.Dispose();
            File.Delete(FilePath);
        }

        private void SelectRoot(bool externalPreviewer)
        {
            MutableAstObjectNode root = Root;
            if (externalPreviewer)
            {
                DesignItem remote = new(root) { HostInstanceId = 1, HostBounds = new Avalonia.Rect(0, 0, 200, 100) };
                Document.DesignSurface.SetDesignTree(remote,
                    new Dictionary<Guid, DesignItem> { [root.Id] = remote },
                    new Dictionary<Control, DesignItem>());
            }

            Document.SetSelectedNode(root.Id, SyncSource.TreeView);
        }

        private void CreateChildItems()
        {
            MutableAstObjectNode root = Root;
            DesignItem rootItem = new(root);
            Dictionary<Guid, DesignItem> items = new() { [root.Id] = rootItem };
            ChildItems = ChildNodes.Select(node => new DesignItem(node)).ToArray();
            foreach (DesignItem item in ChildItems)
            {
                items[item.AstNodeId] = item;
            }

            Document.DesignSurface.SetDesignTree(rootItem, items, new Dictionary<Control, DesignItem>());
        }

        private void SetSelection(int[] children) =>
            Document.DesignSurface.Selection.SetSelection(children.Select(index => ChildItems[index]).ToArray());
    }

    /// <summary>Forwards to the shell host and rejects property edits of one node.</summary>
    private sealed class FailingNodeDesignerHost(IDesignerHost inner, string failingNodeId) : IDesignerHost
    {
        public string? ActiveDocumentPath => inner.ActiveDocumentPath;

        public event EventHandler<DesignerDocumentChangedEventArgs>? ActiveDocumentChanged
        {
            add => inner.ActiveDocumentChanged += value;
            remove => inner.ActiveDocumentChanged -= value;
        }

        public event EventHandler<DesignerSelectionChangedEventArgs>? SelectionChanged
        {
            add => inner.SelectionChanged += value;
            remove => inner.SelectionChanged -= value;
        }

        public Task<IReadOnlyList<DesignerNodeSummary>> GetSelectedNodesAsync(CancellationToken cancellationToken) =>
            inner.GetSelectedNodesAsync(cancellationToken);

        public Task<IReadOnlyList<DesignerNodeSummary>> GetVisualTreeAsync(CancellationToken cancellationToken) =>
            inner.GetVisualTreeAsync(cancellationToken);

        public Task<IReadOnlyList<DesignerNodeSummary>> GetLogicalTreeAsync(CancellationToken cancellationToken) =>
            inner.GetLogicalTreeAsync(cancellationToken);

        public Task<IReadOnlyList<DesignerPropertyInfo>> GetPropertiesAsync(string nodeId, CancellationToken cancellationToken) =>
            inner.GetPropertiesAsync(nodeId, cancellationToken);

        public Task<IReadOnlyList<DesignerEventInfo>> GetEventsAsync(string nodeId, CancellationToken cancellationToken) =>
            inner.GetEventsAsync(nodeId, cancellationToken);

        public Task<bool> SetPropertyAsync(string nodeId, string propertyName, string? value, CancellationToken cancellationToken) =>
            string.Equals(nodeId, failingNodeId, StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(false)
                : inner.SetPropertyAsync(nodeId, propertyName, value, cancellationToken);

        public Task<string?> InsertElementAsync(string typeName, string xmlNamespace, string? parentNodeId,
            CancellationToken cancellationToken) =>
            inner.InsertElementAsync(typeName, xmlNamespace, parentNodeId, cancellationToken);

        public Task<bool> DeleteNodeAsync(string nodeId, CancellationToken cancellationToken) =>
            inner.DeleteNodeAsync(nodeId, cancellationToken);

        public Task<string?> WrapNodeAsync(string nodeId, string wrapperTypeName, string wrapperXmlNamespace,
            CancellationToken cancellationToken) =>
            inner.WrapNodeAsync(nodeId, wrapperTypeName, wrapperXmlNamespace, cancellationToken);

        public Task<bool> SelectNodeAsync(string nodeId, CancellationToken cancellationToken) =>
            inner.SelectNodeAsync(nodeId, cancellationToken);

        public Task<bool> RevealNodeAsync(string nodeId, CancellationToken cancellationToken) =>
            inner.RevealNodeAsync(nodeId, cancellationToken);

        public IDesignerTransaction BeginTransaction(string name) => inner.BeginTransaction(name);
    }

    /// <summary>
    /// Designer host with one Click event per node. Handler assignments wait for
    /// <see cref="PendingHandlerResult" /> when it is set.
    /// </summary>
    private sealed class ScriptedDesignerHost : IDesignerHost, IDesignerEventHost
    {
        public string? ActiveDocumentPath { get; set; } = "Scripted.axaml";
        public IReadOnlyList<DesignerNodeSummary> SelectedNodes { get; set; } = [];
        public IReadOnlyList<DesignerPropertyInfo> Properties { get; set; } = [];
        public Dictionary<string, string?> Handlers { get; } = new(StringComparer.Ordinal);
        public List<string> HandlerRequests { get; } = [];
        public List<string> Navigations { get; } = [];
        public TaskCompletionSource<bool>? PendingHandlerResult { get; set; }
        public bool AcceptHandlers { get; set; } = true;
        public bool CanNavigate { get; set; } = true;

        public event EventHandler<DesignerDocumentChangedEventArgs>? ActiveDocumentChanged { add { } remove { } }

        public event EventHandler<DesignerSelectionChangedEventArgs>? SelectionChanged { add { } remove { } }

        public Task<IReadOnlyList<DesignerNodeSummary>> GetSelectedNodesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelectedNodes);

        public Task<IReadOnlyList<DesignerNodeSummary>> GetVisualTreeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelectedNodes);

        public Task<IReadOnlyList<DesignerNodeSummary>> GetLogicalTreeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SelectedNodes);

        public Task<IReadOnlyList<DesignerPropertyInfo>> GetPropertiesAsync(string nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(Properties);

        public Task<IReadOnlyList<DesignerEventInfo>> GetEventsAsync(string nodeId, CancellationToken cancellationToken)
        {
            string? handler = Handlers.TryGetValue(nodeId, out string? assigned) ? assigned : null;
            return Task.FromResult<IReadOnlyList<DesignerEventInfo>>([new DesignerEventInfo("Click", handler, null)]);
        }

        public Task<bool> SetPropertyAsync(string nodeId, string propertyName, string? value, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<string?> InsertElementAsync(string typeName, string xmlNamespace, string? parentNodeId,
            CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<bool> DeleteNodeAsync(string nodeId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<string?> WrapNodeAsync(string nodeId, string wrapperTypeName, string wrapperXmlNamespace,
            CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<bool> SelectNodeAsync(string nodeId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> RevealNodeAsync(string nodeId, CancellationToken cancellationToken) => Task.FromResult(false);

        public IDesignerTransaction BeginTransaction(string name) => throw new NotSupportedException();

        public async Task<bool> SetEventHandlerAsync(string nodeId, string eventName, string? handlerName,
            CancellationToken cancellationToken)
        {
            HandlerRequests.Add($"{nodeId}.{eventName}={handlerName}");
            bool accepted = PendingHandlerResult is { } pending
                ? await pending.Task.WaitAsync(cancellationToken)
                : AcceptHandlers;
            if (accepted)
            {
                Handlers[nodeId] = handlerName;
            }

            return accepted;
        }

        public Task<bool> NavigateToEventHandlerAsync(string nodeId, string eventName, CancellationToken cancellationToken)
        {
            Navigations.Add($"{nodeId}.{eventName}");
            return Task.FromResult(CanNavigate);
        }
    }
}
