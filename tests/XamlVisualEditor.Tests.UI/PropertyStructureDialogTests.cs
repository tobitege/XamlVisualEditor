using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.PropertyEditorExtension;
using XamlVisualEditor.PropertyEditorExtension.Views;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;

namespace XamlVisualEditor.Tests.UI;

public sealed class PropertyStructureDialogTests
{
    [AvaloniaTheory]
    [InlineData("Canvas.Left", false)]
    [InlineData("av:Grid.Row", false)]
    [InlineData("Width", true)]
    [InlineData("Canvas.Left", true)]
    [InlineData("av:Grid.Row", true)]
    public async Task Structure_Draft_Rejects_Invalid_Attached_Values_And_Duplicate_Assignments(
        string member, bool duplicate)
    {
        const string original = "<Button xmlns=\"https://github.com/avaloniaui\" xmlns:av=\"https://github.com/avaloniaui\"><Button.Template><ControlTemplate TargetType=\"Button\"><Border Width=\"80\" Height=\"40\" /></ControlTemplate></Button.Template></Button>";
        string path = Path.Combine(Path.GetTempPath(), $"xve-structure-validation-{Guid.NewGuid():N}.axaml");
        await File.WriteAllTextAsync(path, original);
        using MainWindowViewModel main = new(metadataService: new TypeMetadataService());
        await main.OpenFileAsync(path);
        DesignerDocumentViewModel document = Assert.IsType<DesignerDocumentViewModel>(main.ActiveDesignerDocument);
        using ShellDesignerHost host = new(main);
        DesignerPropertyStructure snapshot = await host.OpenPropertyStructureAsync(
            document.SyncEngine.CurrentDocument!.Root!.Id.ToString("D"), "Template", CancellationToken.None);
        using PropertyStructureEditorViewModel editor = new(host, snapshot);
        bool closed = false;
        editor.CloseRequested += () => closed = true;
        editor.SelectedNode = editor.Roots.SelectMany(node => node.DescendantsAndSelf()).Single(node => node.Label == "Border");
        editor.AttributeName = member;
        editor.AttributeValue = duplicate ? "20" : "invalid";
        await editor.SetAttributeCommand.Execute();
        if (duplicate)
        {
            editor.PropertyName = member;
            await editor.AddPropertyCommand.Execute();
            Assert.Null(editor.Error);
            editor.TextValue = "40";
        }

        await editor.ApplyCommand.Execute();
        Assert.Contains(member[(member.IndexOf(':') + 1)..], editor.Error);
        if (duplicate) Assert.Contains("more than once", editor.Error);
        Assert.False(closed);
        Assert.Equal(original, document.SyncEngine.CurrentText);
        Assert.Equal(original, document.CodeEditor.Document.Text);
        Assert.Equal(0, document.SyncEngine.UndoRedo.UndoCount);

        if (duplicate) await editor.RemoveCommand.Execute();
        else
        {
            editor.AttributeValue = "20";
            await editor.SetAttributeCommand.Execute();
        }
        await editor.ApplyCommand.Execute();
        Assert.Null(editor.Error);
        Assert.True(closed);
        Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
        string applied = document.SyncEngine.GetCurrentText();
        Assert.NotEqual(original, applied);
        document.SyncEngine.Undo();
        Assert.Equal(original, document.SyncEngine.CurrentText);
        document.SyncEngine.Redo();
        Assert.Equal(applied, document.SyncEngine.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structure_Draft_Cannot_Overwrite_Changed_Or_Switched_Document(bool switchDocument)
    {
        const string original = "<Button xmlns=\"https://github.com/avaloniaui\"><Button.Template><ControlTemplate TargetType=\"Button\"><Border Width=\"80\" /></ControlTemplate></Button.Template></Button>";
        string path = Path.Combine(Path.GetTempPath(), $"xve-structure-lifetime-{Guid.NewGuid():N}.axaml");
        await File.WriteAllTextAsync(path, original);
        using MainWindowViewModel main = new(metadataService: new TypeMetadataService());
        await main.OpenFileAsync(path);
        DesignerDocumentViewModel document = Assert.IsType<DesignerDocumentViewModel>(main.ActiveDesignerDocument);
        using ShellDesignerHost host = new(main);
        DesignerPropertyStructure snapshot = await host.OpenPropertyStructureAsync(
            document.SyncEngine.CurrentDocument!.Root!.Id.ToString("D"), "Template", CancellationToken.None);
        using PropertyStructureEditorViewModel editor = new(host, snapshot);
        bool closed = false;
        editor.CloseRequested += () => closed = true;
        editor.SelectedNode = editor.Roots.SelectMany(node => node.DescendantsAndSelf()).Single(node => node.Label == "Border");
        editor.AttributeName = "Width";
        editor.AttributeValue = "120";
        await editor.SetAttributeCommand.Execute();

        if (switchDocument)
        {
            string otherPath = Path.Combine(Path.GetTempPath(), $"xve-structure-other-{Guid.NewGuid():N}.axaml");
            await File.WriteAllTextAsync(otherPath, original);
            await main.OpenFileAsync(otherPath);
        }
        else
        {
            using var edit = document.SyncEngine.BeginEditTransaction("Change outside structure editor");
            document.SyncEngine.CurrentDocument!.Root!.SetPropertyValue("Tag", "Changed elsewhere");
            edit.Commit();
        }
        DesignerDocumentViewModel active = Assert.IsType<DesignerDocumentViewModel>(main.ActiveDesignerDocument);
        string documentText = document.SyncEngine.GetCurrentText();
        string activeText = active.SyncEngine.GetCurrentText();
        int undoCount = document.SyncEngine.UndoRedo.UndoCount;
        int activeUndoCount = active.SyncEngine.UndoRedo.UndoCount;

        await editor.ApplyCommand.Execute();
        Assert.Contains("document changed", editor.Error);
        Assert.False(closed);
        Assert.Equal(documentText, document.SyncEngine.CurrentText);
        Assert.Equal(activeText, active.SyncEngine.CurrentText);
        Assert.Equal(undoCount, document.SyncEngine.UndoRedo.UndoCount);
        Assert.Equal(activeUndoCount, active.SyncEngine.UndoRedo.UndoCount);
        await editor.CancelCommand.Execute();
        Assert.True(closed);
        Assert.Equal(documentText, document.SyncEngine.CurrentText);
        Assert.Equal(activeText, active.SyncEngine.CurrentText);
    }

    [AvaloniaTheory]
    [InlineData("TabControl", "Items", "TabItem")]
    [InlineData("Menu", "Items", "MenuItem")]
    [InlineData("Button", "Template", "Border")]
    [InlineData("DataGrid", "Columns", "DataGridTextColumn")]
    [InlineData("UserControl", "Resources", "SolidColorBrush")]
    [InlineData("LibraryGrid", "Children", "CaptionControl")]
    public async Task Structure_Dialog_Validates_Corrects_Cancels_And_Preserves_History(
        string ownerType, string propertyName, string editedType)
    {
        string content = ownerType switch
        {
            "TabControl" => "<TabItem Header=\"First\"><TextBlock Text=\"Keep this content\" /></TabItem><TabItem Header=\"Second\" />",
            "Menu" => "<MenuItem Header=\"File\"><MenuItem Header=\"Open\" /><MenuItem Header=\"Close\" /></MenuItem>",
            "DataGrid" => "<DataGrid.Columns><DataGridTextColumn Header=\"First\" Binding=\"{Binding Name}\" /><DataGridTextColumn Header=\"Second\" Binding=\"{Binding City}\" /></DataGrid.Columns>",
            "UserControl" => "<UserControl.Resources><SolidColorBrush x:Key=\"Accent\" Color=\"SteelBlue\" /></UserControl.Resources><Border Background=\"{StaticResource Accent}\" />",
            "LibraryGrid" => "<lib:CaptionControl Content=\"First\" Caption=\"Original library caption\"><lib:CaptionControl.Resources><SolidColorBrush x:Key=\"Accent\" Color=\"SteelBlue\" /></lib:CaptionControl.Resources></lib:CaptionControl><Button Content=\"Keep standard sibling\" />",
            _ => "<Button.Template><ControlTemplate TargetType=\"Button\"><Border Background=\"SteelBlue\"><ContentPresenter Content=\"{TemplateBinding Content}\" /></Border></ControlTemplate></Button.Template>"
        };
        string valueProperty = propertyName == "Resources" ? "Color" : "Width";
        string validValue = propertyName == "Resources" ? "Coral" : "180";
        string captionAttribute = propertyName switch { "Resources" => "x:Key", "Children" => "Content", _ => "Header" };
        string assemblyName = ownerType switch
        {
            "DataGrid" => typeof(Avalonia.Controls.DataGrid).Assembly.GetName().Name!,
            "UserControl" => typeof(Avalonia.Media.SolidColorBrush).Assembly.GetName().Name!,
            "LibraryGrid" => typeof(XamlVisualEditor.TestControls.CaptionControl).Assembly.GetName().Name!,
            _ => typeof(Control).Assembly.GetName().Name!
        };
        string ownerTag = ownerType == "LibraryGrid" ? "lib:LibraryGrid" : ownerType;
        string libraryDeclaration = ownerType == "LibraryGrid"
            ? $" xmlns:lib=\"clr-namespace:XamlVisualEditor.TestControls;assembly={assemblyName}\"" : string.Empty;
        string original = $"<{ownerTag} xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"{libraryDeclaration}>{content}</{ownerTag}>";
        string filePath = Path.Combine(Path.GetTempPath(), $"xve-structure-{Guid.NewGuid():N}.axaml");
        await File.WriteAllTextAsync(filePath, original);
        TypeMetadataService metadata = new();
        using MainWindowViewModel main = new(metadataService: metadata);
        await main.OpenFileAsync(filePath);
        DesignerDocumentViewModel document = Assert.IsType<DesignerDocumentViewModel>(main.ActiveDesignerDocument);
        using ShellDesignerHost host = new(main);
        FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        Window? window = null;
        try
        {
            DesignerPropertyStructure snapshot = await host.OpenPropertyStructureAsync(
                document.SyncEngine.CurrentDocument!.Root!.Id.ToString("D"), propertyName, CancellationToken.None);
            using PropertyStructureEditorViewModel editor = new(host, snapshot);
            PropertyStructureEditorView view = new() { DataContext = editor };
            window = new Window { Content = view, Width = 860, Height = 720 };
            editor.CloseRequested += window.Close;
            window.Show();
            window.UpdateLayout();
            Avalonia.Controls.TreeView tree = view.GetVisualDescendants().OfType<Avalonia.Controls.TreeView>().Single();
            string selectedLabel = ownerType switch
            {
                "TabControl" => "TabItem (First)",
                "Menu" => "MenuItem (File)",
                "DataGrid" => "DataGridTextColumn (First)",
                "UserControl" => "SolidColorBrush (Accent)",
                "LibraryGrid" => "CaptionControl (First)",
                _ => "Border"
            };
            tree.SelectedItem = editor.Roots.SelectMany(node => node.DescendantsAndSelf())
                .First(node => node.Label == selectedLabel);
            window.UpdateLayout();
            TreeViewItem selectedContainer = tree.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(item => ReferenceEquals(item.DataContext, editor.SelectedNode));
            selectedContainer.IsExpanded = false;
            Assert.False(editor.SelectedNode!.IsExpanded);

            void Enter(string placeholder, string value)
            {
                TextBox field = view.GetVisualDescendants().OfType<TextBox>()
                    .Single(control => control.PlaceholderText == placeholder);
                Assert.True(field.Focus());
                field.SelectAll();
                window.KeyTextInput(value);
            }
            async Task Press(string label)
            {
                Button button = view.GetVisualDescendants().OfType<Button>()
                    .Single(control => Equals(control.Content, label));
                PropertyStructureEditorViewModel current = Assert.IsType<PropertyStructureEditorViewModel>(view.DataContext);
                ReactiveCommand<RxVoid, RxVoid> command = Assert.IsType<ReactiveCommand<RxVoid, RxVoid>>(button.Command);
                await button.GetObservable(InputElement.IsEffectivelyEnabledProperty)
                    .FirstAsync(enabled => enabled).Timeout(TimeSpan.FromSeconds(5));
                Assert.True(button.Focus(), $"{label}: visible={window.IsVisible}, active={window.IsActive}, " +
                    $"enabled={button.IsEnabled}, effective={button.IsEffectivelyEnabled}, " +
                    $"focused={button.IsFocused}, busy={current.IsBusy}, " +
                    $"canExecute={button.Command?.CanExecute(button.CommandParameter)}, error={current.Error}");
                window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
                await command.IsExecuting.FirstAsync(executing => !executing);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            void SetValue(string value)
            {
                Enter("Attribute, e.g. x:Key or Width", valueProperty);
                Enter("Value or binding expression; empty is a local value", value);
            }

            if (propertyName == "Columns")
            {
                ComboBox picker = view.GetVisualDescendants().OfType<ComboBox>().Single();
                picker.SelectedItem = editor.AvailableTypes.Single(type => type.DisplayName == "Button [Avalonia.Controls]");
                await Press("Add sibling");
                Assert.Null(editor.Error);
                Assert.Equal("Button", editor.SelectedNode!.Label);
                await Press("Apply");
                Assert.True(window.IsVisible, "An incompatible collection item must leave the draft open for correction.");
                Assert.Contains("DataGridColumn", editor.Error);
                Assert.Equal(original, document.SyncEngine.CurrentText);
                Assert.Equal(0, document.SyncEngine.UndoRedo.UndoCount);
                await Press("Remove");
                tree.SelectedItem = editor.Roots.SelectMany(node => node.DescendantsAndSelf())
                    .Single(node => node.Label == selectedLabel);
            }

            if (propertyName != "Template")
            {
                ComboBox picker = view.GetVisualDescendants().OfType<ComboBox>().Single();
                picker.SelectedItem = editor.AvailableTypes.Single(type => type.DisplayName == $"{editedType} [{assemblyName}]");
                await Press("Add sibling");
                Enter("Attribute, e.g. x:Key or Width", captionAttribute);
                Enter("Value or binding expression; empty is a local value", "Added");
                await Press("Set attribute");
                await Press("Move up");
                Assert.Equal($"{editedType} (Added)", editor.Roots[0].Children[0].Label);
                tree.SelectedItem = editor.Roots.SelectMany(node => node.DescendantsAndSelf())
                    .Single(node => node.Label == selectedLabel);
                Assert.False(editor.SelectedNode!.IsExpanded);
            }

            if (ownerType == "LibraryGrid")
            {
                Enter("Attribute, e.g. x:Key or Width", "Caption");
                Enter("Value or binding expression; empty is a local value", "Updated library caption");
                await Press("Set attribute");
            }

            SetValue("invalid");
            await Press("Set attribute");
            Assert.Contains(editor.Attributes, attribute => attribute.Display == $"{valueProperty} = invalid");
            Assert.False(editor.SelectedNode!.IsExpanded);
            window.UpdateLayout();
            Assert.False(tree.GetVisualDescendants().OfType<TreeViewItem>()
                .Single(item => ReferenceEquals(item.DataContext, editor.SelectedNode)).IsExpanded);
            await Press("Apply");
            Assert.True(window.IsVisible);
            Assert.Contains(valueProperty, editor.Error);
            Assert.Equal(original, document.SyncEngine.CurrentText);
            Assert.Equal(0, document.SyncEngine.UndoRedo.UndoCount);

            SetValue(validValue);
            await Press("Set attribute");
            await Press("Apply");
            Assert.Null(editor.Error);
            Assert.False(window.IsVisible);
            string applied = Assert.IsType<string>(document.SyncEngine.CurrentText);
            Assert.Contains($"{valueProperty}=\"{validValue}\"", applied);
            Assert.DoesNotContain("invalid", applied);
            if (propertyName != "Template")
            {
                XName captionName = propertyName == "Resources"
                    ? XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml") : XName.Get(captionAttribute);
                Assert.Contains(XElement.Parse(applied).Descendants(), element => element.Name.LocalName == editedType
                    && (string?)element.Attribute(captionName) == "Added");
            }
            if (ownerType == "LibraryGrid")
            {
                Assert.Contains("Caption=\"Updated library caption\"", applied);
                Assert.Contains("Content=\"Keep standard sibling\"", applied);
                Assert.Contains("Color=\"SteelBlue\"", applied);
            }
            Assert.Equal(applied, document.CodeEditor.Document.Text);
            Assert.True(document.IsModified);
            Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
            document.SyncEngine.Undo();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(original, document.SyncEngine.CurrentText);
            Assert.Equal(original, document.CodeEditor.Document.Text);
            document.SyncEngine.Redo();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(applied, document.SyncEngine.CurrentText);
            Assert.Equal(applied, document.CodeEditor.Document.Text);

            DesignerPropertyStructure updated = await host.OpenPropertyStructureAsync(
                document.SyncEngine.CurrentDocument!.Root!.Id.ToString("D"), propertyName, CancellationToken.None);
            using PropertyStructureEditorViewModel canceled = new(host, updated);
            view = new PropertyStructureEditorView { DataContext = canceled };
            window = new Window { Content = view, Width = 860, Height = 720 };
            canceled.CloseRequested += window.Close;
            window.Show();
            window.UpdateLayout();
            Avalonia.Controls.TreeView cancelTree = view.GetVisualDescendants().OfType<Avalonia.Controls.TreeView>().Single();
            cancelTree.SelectedItem = canceled.Roots.SelectMany(node => node.DescendantsAndSelf())
                .First(node => node.Label == selectedLabel);
            await Press("Remove");
            await Press("Cancel");
            Assert.False(window.IsVisible);
            Assert.Equal(applied, document.SyncEngine.CurrentText);
            Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);

            await main.SaveDocumentCommand.Execute();
            using MainWindowViewModel reopened = new(metadataService: metadata);
            await reopened.OpenFileAsync(filePath);
            Assert.Equal(applied, reopened.ActiveDesignerDocument!.SyncEngine.CurrentText);
        }
        finally
        {
            window?.Close();
            Application.Current.Styles.Remove(theme);
        }
    }
}
