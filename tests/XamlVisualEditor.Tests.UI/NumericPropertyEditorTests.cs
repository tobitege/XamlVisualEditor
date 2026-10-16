using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.PropertyEditorExtension;
using XamlVisualEditor.PropertyEditorExtension.Views;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class NumericPropertyEditorTests
{
    [AvaloniaTheory]
    [InlineData("de-DE", "160,5", true)]
    [InlineData("en-US", "160.5", false)]
    public async Task PropertyInputPreservesInvalidDraftsAndResourceExpressions(string cultureName, string input, bool initiallySet)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        FluentTheme theme = new();
        StyleInclude gridTheme = new((Uri?)null)
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml")
        };
        Application.Current!.Styles.Add(theme);
        Application.Current.Styles.Add(gridTheme);
        Window? window = null;
        string path = Path.Combine(Path.GetTempPath(), "xve-numeric-input-" + Guid.NewGuid().ToString("N") + ".axaml");
        try
        {
            string initialBackground = initiallySet ? " Background=\"#FF336699\"" : string.Empty;
            string original = "<Button xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Width=\"160\" Height=\"40\""
                + initialBackground + "><Button.Resources><SolidColorBrush x:Key=\"Accent\" Color=\"SteelBlue\" /></Button.Resources></Button>";
            await File.WriteAllTextAsync(path, original);
            TypeMetadataService metadata = new();
            metadata.LoadAssembly(typeof(Control).Assembly.Location);
            metadata.LoadAssembly(typeof(Brush).Assembly.Location);
            using DesignerDocumentViewModel document = new(path, metadata) { UseExternalPreviewer = true };
            await document.LoadAsync();
            using MainWindowViewModel main = new(metadataService: metadata);
            main.Documents.Add(document);
            main.ActiveDocument = document;
            var root = document.SyncEngine.CurrentDocument!.Root!;
            DesignItem item = new(root) { HostInstanceId = 1, HostBounds = new Rect(0, 0, 160, 40) };
            document.DesignSurface.SetDesignTree(item, new Dictionary<Guid, DesignItem> { [root.Id] = item }, new Dictionary<Control, DesignItem>());
            document.SetSelectedNode(root.Id, SyncSource.TreeView);
            using ShellDesignerHost host = new(main);
            using PropertyEditorPanelViewModel inspector = new(host, new PropertyEditorRegistry());
            await inspector.InitializeAsync(CancellationToken.None);
            PropertyEditorPanelView view = new() { DataContext = inspector };
            window = new Window { Content = view, Width = 600, Height = 800 };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            TextBox search = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(),
                control => control.PlaceholderText == "Search properties...");
            search.Text = "Width";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            TextBox textBox = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(),
                control => control.DataContext is PropertyEntryViewModel { Name: "Width" } && control.IsEffectivelyVisible);
            PropertyEntryViewModel entry = Assert.IsType<PropertyEntryViewModel>(textBox.DataContext);
            Assert.Equal("160", textBox.Text);
            Assert.Equal("160", entry.Value);
            Assert.True(textBox.Focus());
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
            window.KeyTextInput("oops");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await entry.WhenAnyValue(value => value.HasValidationError).Where(value => value)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(5)).ToTask();
            Assert.Equal("oops", textBox.Text);
            Assert.Equal("oops", entry.Value);
            Assert.Equal(original, document.SyncEngine.CurrentText);
            Assert.Equal(0, document.SyncEngine.UndoRedo.UndoCount);
            Assert.True(textBox.IsFocused);
            Assert.Same(textBox, Assert.Single(view.GetVisualDescendants().OfType<TextBox>(),
                control => control.DataContext == entry && control.IsEffectivelyVisible));

            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
            window.KeyTextInput(input);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await document.WhenAnyValue(value => value.IsModified).Where(value => value)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(5)).ToTask();
            Assert.False(entry.HasValidationError);
            Assert.Equal("160.5", document.SyncEngine.CurrentDocument!.Root!.GetPropertyValue("Width"));
            Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
            document.SyncEngine.Undo();
            Assert.Equal(original, document.SyncEngine.CurrentText);

            search.Text = "Background";
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            TextBox brushInput = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(),
                control => control.DataContext is PropertyEntryViewModel { Name: "Background" } && control.IsEffectivelyVisible);
            PropertyEntryViewModel background = Assert.IsType<PropertyEntryViewModel>(brushInput.DataContext);
            Assert.Equal(initiallySet, background.IsSet);
            Assert.True(brushInput.Focus());
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
            window.KeyTextInput("{");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(brushInput.IsFocused);
            window.KeyTextInput("DynamicResource Accent}");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await document.WhenAnyValue(value => value.IsModified).Where(value => value)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(5)).ToTask();
            Assert.Equal("{DynamicResource Accent}", document.SyncEngine.CurrentDocument!.Root!.GetPropertyValue("Background"));
            Assert.StartsWith("Dynamic resource:", background.ValueSourceDescription);

            await background.SetNullCommand.Execute();
            await background.WhenAnyValue(value => value.Value).Where(value => value == "{x:Null}")
                .FirstAsync().Timeout(TimeSpan.FromSeconds(5)).ToTask();
            Assert.Equal("{x:Null}", document.SyncEngine.CurrentDocument.Root.GetPropertyValue("Background"));
            Assert.StartsWith("Explicit null:", background.ValueSourceDescription);

            await background.ResetCommand.Execute();
            await background.WhenAnyValue(value => value.IsSet).Where(value => !value)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(5)).ToTask();
            Assert.Null(document.SyncEngine.CurrentDocument.Root.GetPropertyValue("Background"));
            Assert.StartsWith("Style/default:", background.ValueSourceDescription);
            document.SyncEngine.Undo();
            document.SyncEngine.Undo();
            document.SyncEngine.Undo();
            Assert.Equal(original, document.SyncEngine.CurrentText);
        }
        finally
        {
            window?.Close();
            Application.Current.Styles.Remove(gridTheme);
            Application.Current.Styles.Remove(theme);
            CultureInfo.CurrentCulture = previousCulture;
            File.Delete(path);
        }
    }
}
