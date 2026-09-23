using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.PropertyEditorExtension;
using XamlVisualEditor.PropertyEditorExtension.Views;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class PropertyGroupExpansionTests
{
    [AvaloniaFact(Timeout = 30000)]
    public async Task CategoriesStartCollapsedAndThePreferenceExpandsThem()
    {
        FluentTheme theme = new();
        StyleInclude gridTheme = new((Uri?)null)
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml")
        };
        Application.Current!.Styles.Add(theme);
        Application.Current.Styles.Add(gridTheme);
        Window? window = null;
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "xve-group-expansion-" + Guid.NewGuid().ToString("N") + ".axaml");
            await File.WriteAllTextAsync(path,
                "<Button xmlns=\"https://github.com/avaloniaui\" Width=\"160\" Height=\"40\" />");
            TypeMetadataService metadata = new();
            metadata.LoadAssembly(typeof(Button).Assembly.Location);
            using DesignerDocumentViewModel document = new(path, metadata) { UseExternalPreviewer = true };
            await document.LoadAsync();
            using MainWindowViewModel main = new(metadataService: metadata);
            main.Documents.Add(document);
            main.ActiveDocument = document;
            var root = document.SyncEngine.CurrentDocument!.Root!;
            DesignItem item = new(root) { HostInstanceId = 1, HostBounds = new Rect(0, 0, 160, 40) };
            document.DesignSurface.SetDesignTree(item,
                new Dictionary<Guid, DesignItem> { [root.Id] = item }, new Dictionary<Control, DesignItem>());
            document.SetSelectedNode(root.Id, SyncSource.TreeView);
            using ShellDesignerHost host = new(main);
            using PropertyEditorPanelViewModel inspector = new(host, new PropertyEditorRegistry());
            await inspector.InitializeAsync(CancellationToken.None);
            Assert.True(inspector.CollapseOtherCategoriesOnSelection);
            PropertyRowViewModel[] rows = inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>().ToArray();
            Assert.Contains(rows, row => row.IsHeader && row.IsPinned && row.IsExpanded);
            PropertyRowViewModel category = Assert.Single(rows.Where(row => row.IsHeader && row.CanCollapse)
                .Take(1));
            Assert.False(category.IsExpanded);
            Assert.DoesNotContain(rows, row => row.Property is not null && row.GroupName == category.GroupName);
            PropertyEditorPanelView view = new() { DataContext = inspector };
            window = new Window { Content = view, Width = 520, Height = 660 };
            window.Show();
            await FlushAsync(window);
            Assert.Single(view.GetVisualDescendants().OfType<DataGrid>(), grid => grid.IsVisible);

            category.IsExpanded = true;
            await FlushAsync(window);
            rows = inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>().ToArray();
            Assert.Contains(rows, row => row.Property is not null && row.GroupName == category.GroupName);

            inspector.CollapseOtherCategoriesOnSelection = false;
            await FlushAsync(window);
            rows = inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>().ToArray();
            Assert.All(rows.Where(row => row.IsHeader && row.CanCollapse), row => Assert.True(row.IsExpanded));

            inspector.CollapseOtherCategoriesOnSelection = true;
            await FlushAsync(window);
            rows = inspector.GroupedPropertiesView.Cast<PropertyRowViewModel>().ToArray();
            Assert.All(rows.Where(row => row.IsHeader && row.CanCollapse), row => Assert.False(row.IsExpanded));
        }
        finally
        {
            window?.Close();
            Application.Current.Styles.Remove(gridTheme);
            Application.Current.Styles.Remove(theme);
        }
    }

    private static async Task FlushAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }
}
