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
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.PropertyEditorExtension;
using XamlVisualEditor.PropertyEditorExtension.Views;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class MixedValuesPropertyEditorTests
{
    [AvaloniaFact]
    public async Task Value_editors_show_the_mixed_values_placeholder_and_a_mixed_bool_click_writes_a_value()
    {
        FluentTheme theme = new();
        StyleInclude gridTheme = new((Uri?)null)
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml")
        };
        Application.Current!.Styles.Add(theme);
        Application.Current.Styles.Add(gridTheme);
        Window? window = null;
        string path = Path.Combine(Path.GetTempPath(), "xve-mixed-values-" + Guid.NewGuid().ToString("N") + ".axaml");
        try
        {
            const string original = """
                <StackPanel xmlns="https://github.com/avaloniaui">
                  <Button Width="96" Content="Cancel" Margin="1" HorizontalAlignment="Left" IsDefault="False" />
                  <Button Width="120" Content="Save" Margin="2" HorizontalAlignment="Right" IsDefault="True" />
                </StackPanel>
                """;
            await File.WriteAllTextAsync(path, original);
            TypeMetadataService metadata = new();
            metadata.LoadAssembly(typeof(Control).Assembly.Location);
            metadata.LoadAssembly(typeof(Brush).Assembly.Location);
            using DesignerDocumentViewModel document = new(path, metadata);
            await document.LoadAsync();
            using MainWindowViewModel main = new(metadataService: metadata);
            main.Documents.Add(document);
            main.ActiveDocument = document;
            MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
            MutableAstObjectNode[] buttons = root.Children.OfType<MutableAstObjectNode>().ToArray();
            DesignItem[] buttonItems = buttons.Select(node => new DesignItem(node)).ToArray();
            Dictionary<Guid, DesignItem> items = buttonItems.ToDictionary(item => item.AstNodeId);
            items[root.Id] = new DesignItem(root);
            document.DesignSurface.SetDesignTree(items[root.Id], items, new Dictionary<Control, DesignItem>());
            document.DesignSurface.Selection.SetSelection(buttonItems);
            using ShellDesignerHost host = new(main);
            using PropertyEditorPanelViewModel inspector = new(host, new PropertyEditorRegistry());
            await inspector.InitializeAsync(CancellationToken.None);
            PropertyEditorPanelView view = new() { DataContext = inspector };
            window = new Window { Content = view, Width = 600, Height = 800 };
            window.Show();
            await FlushAsync(window);
            TextBox search = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(),
                control => control.PlaceholderText == UiStrings.SearchProperties);
            string mixed = UiStrings.MixedValuesPlaceholder;

            TextBox width = await ShowEditorAsync<TextBox>(window, view, search, "Width");
            Assert.True(Assert.IsType<PropertyEntryViewModel>(width.DataContext).IsMixed);
            Assert.Equal(mixed, width.PlaceholderText);
            Assert.True(ShowsText(width, mixed));

            TextBox content = await ShowEditorAsync<TextBox>(window, view, search, "Content");
            Assert.Equal(mixed, content.PlaceholderText);
            Assert.True(ShowsText(content, mixed));

            TextBox margin = await ShowEditorAsync<TextBox>(window, view, search, "Margin");
            Assert.Equal(mixed, margin.PlaceholderText);
            Assert.True(ShowsText(margin, mixed));
            TextBox padding = await ShowEditorAsync<TextBox>(window, view, search, "Padding");
            Assert.False(Assert.IsType<PropertyEntryViewModel>(padding.DataContext).IsMixed);
            Assert.Equal(UiStrings.ThicknessOrder, padding.PlaceholderText);

            ComboBox alignment = await ShowEditorAsync<ComboBox>(window, view, search, "HorizontalAlignment");
            Assert.Equal(mixed, alignment.PlaceholderText);
            Assert.True(ShowsText(alignment, mixed));

            // A mixed bool has no value: the check box shows the indeterminate state, and a click writes False.
            CheckBox isDefault = await ShowEditorAsync<CheckBox>(window, view, search, "IsDefault");
            PropertyEntryViewModel isDefaultEntry = Assert.IsType<PropertyEntryViewModel>(isDefault.DataContext);
            Assert.True(isDefaultEntry.IsMixed);
            Assert.Null(isDefaultEntry.BoolValue);
            Assert.Null(isDefault.IsChecked);
            Assert.Contains(":indeterminate", isDefault.Classes);
            Point center = isDefault.TranslatePoint(new Point(isDefault.Bounds.Width / 2, isDefault.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            await document.WhenAnyValue(value => value.IsModified).Where(value => value)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(5)).ToTask();
            Assert.Equal(new string?[] { "False", "False" },
                buttons.Select(node => node.GetPropertyValue("IsDefault")).ToArray());
            Assert.False(isDefaultEntry.IsMixed);
            Assert.False(isDefaultEntry.BoolValue);
        }
        finally
        {
            window?.Close();
            Application.Current.Styles.Remove(gridTheme);
            Application.Current.Styles.Remove(theme);
            File.Delete(path);
        }
    }

    private static async Task<TControl> ShowEditorAsync<TControl>(Window window, PropertyEditorPanelView view, TextBox search,
        string propertyName)
        where TControl : Control
    {
        search.Text = propertyName;
        await FlushAsync(window);
        return Assert.Single(view.GetVisualDescendants().OfType<TControl>(),
            control => control.DataContext is PropertyEntryViewModel entry && entry.Name == propertyName
                && control.IsEffectivelyVisible);
    }

    /// <summary>Determines whether a visible text block inside the control, such as its placeholder, shows the text.</summary>
    private static bool ShowsText(Control control, string text) =>
        control.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == text && block.IsEffectivelyVisible);

    private static async Task FlushAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }
}
