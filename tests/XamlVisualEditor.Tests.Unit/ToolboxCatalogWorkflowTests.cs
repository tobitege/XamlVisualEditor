using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.TestControls;
using XamlVisualEditor.ToolboxExtension;
using XamlVisualEditor.ToolboxExtension.Views;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ToolboxCatalogWorkflowTests
{
    [AvaloniaFact]
    public async Task ProjectControlsAreGroupedByAssemblyAndRemainUsableAcrossFiltersAndWorkspaces()
    {
        TypeMetadataService metadata = new();
        string libraryPath = typeof(CaptionControl).Assembly.Location;
        string secondLibraryPath = typeof(ToolboxPanelView).Assembly.Location;
        string libraryName = Assert.IsType<string>(typeof(CaptionControl).Assembly.GetName().Name);
        string secondLibraryName = Assert.IsType<string>(typeof(ToolboxPanelView).Assembly.GetName().Name);
        metadata.LoadAssembly(libraryPath);
        metadata.LoadAssembly(secondLibraryPath);
        MetadataControlCatalog catalog = new(metadata);
        catalog.SetWorkspaceAssemblies(new[] { libraryPath, secondLibraryPath });
        SynchronizationContext context = Assert.IsAssignableFrom<SynchronizationContext>(SynchronizationContext.Current);
        using ToolboxPanelViewModel model = new(new CommandRegistry(), new InMemorySettingsStore(), catalog,
            new SynchronizationContextScheduler(context));
        Application application = Assert.IsAssignableFrom<Application>(Application.Current);
        FluentTheme theme = new();
        // Register the theme before controls attach to a logical root and resolve their implicit themes.
        application.Styles.Add(theme);
        Window? window = null;
        try
        {
            ToolboxPanelView view = new() { DataContext = model };
            window = new Window { Width = 480, Height = 800, Content = view, RequestedThemeVariant = ThemeVariant.Light };
            window.Show();
            await model.ReloadAsync();
            FlushLayout(window);

            string[] expectedAssemblies = model.Items.Select(item => item.AssemblyName).Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ThenBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedAssemblies, model.AssemblyGroups.Select(group => group.AssemblyName).ToArray());
            foreach (ToolboxAssemblyGroup group in model.AssemblyGroups)
            {
                Assert.All(group.Items, item => Assert.Equal(group.AssemblyName, item.AssemblyName));
                Assert.Equal($"{group.AssemblyName} ({group.Items.Count})", GetExpander(view, group).Header);
                AssertAlphabetical(GetControlList(view, group));
            }

            ToolboxAssemblyGroup libraryGroup = Assert.Single(model.AssemblyGroups, group => group.AssemblyName == libraryName);
            ToolboxAssemblyGroup secondLibraryGroup = Assert.Single(model.AssemblyGroups, group => group.AssemblyName == secondLibraryName);
            ToolboxPanelItem standardItem = model.StandardItems[0];
            ToolboxAssemblyGroup standardGroup = Assert.Single(model.AssemblyGroups, group => group.AssemblyName == standardItem.AssemblyName);
            ListBox standard = GetControlList(view, standardGroup);
            ListBox thirdParty = GetControlList(view, libraryGroup);
            ListBox secondLibrary = GetControlList(view, secondLibraryGroup);
            Assert.All(libraryGroup.Items, item => Assert.False(item.IsStandardControl));
            Assert.All(secondLibraryGroup.Items, item => Assert.False(item.IsStandardControl));
            Assert.DoesNotContain(model.ThirdPartyItems, item => item.FullName == typeof(AbstractControl).FullName);
            Assert.DoesNotContain(model.ThirdPartyItems, item => item.FullName == typeof(ServiceControl).FullName);
            Assert.DoesNotContain(model.ThirdPartyItems, item => item.FullName == typeof(HiddenControl).FullName);

            standard.SelectedItem = standardItem;
            ToolboxPanelItem externalButton = Assert.Single(libraryGroup.Items,
                item => item.FullName == typeof(TestControls.Button).FullName);
            thirdParty.SelectedItem = externalButton;
            Assert.Same(externalButton, model.SelectedItem);
            Assert.Null(standard.SelectedItem);
            ToolboxPanelItem secondLibraryControl = Assert.Single(secondLibraryGroup.Items,
                item => item.FullName == typeof(ToolboxPanelView).FullName);
            secondLibrary.SelectedItem = secondLibraryControl;
            Assert.Same(secondLibraryControl, model.SelectedItem);
            Assert.Null(thirdParty.SelectedItem);
            thirdParty.SelectedItem = externalButton;
            Assert.Same(externalButton, model.SelectedItem);
            Assert.Null(secondLibrary.SelectedItem);

            MutableAstObjectNode inserted = new()
            {
                TypeName = externalButton.CommandArguments[0],
                XmlNamespace = externalButton.CommandArguments[1]
            };
            ControlFactory renderer = new(metadata);
            Assert.IsType<TestControls.Button>(renderer.CreateControlTree(inserted));
            inserted.XmlNamespace = "clr-namespace:Missing.Controls;assembly=Missing.Library";
            Assert.IsNotType<Avalonia.Controls.Button>(renderer.CreateControlTree(inserted));

            TextBox search = Assert.IsType<TextBox>(view.FindControl<TextBox>("ControlSearch"));
            search.Text = "library caption";
            FlushLayout(window);
            Assert.Same(libraryGroup, Assert.Single(model.AssemblyGroups));
            Assert.Equal(typeof(CaptionControl).FullName, Assert.Single(model.ThirdPartyItems).FullName);
            Assert.Equal(typeof(CaptionControl).FullName,
                Assert.Single(GetControlList(view, libraryGroup).Items.OfType<ToolboxPanelItem>()).FullName);
            await model.ClearFiltersCommand.Execute().ToTask();
            model.SelectedLibrary = Assert.Single(model.Libraries, option => option.Value == libraryName);
            model.SelectedCategory = Assert.Single(model.Categories, option => option.Value == "Library content");
            FlushLayout(window);
            Assert.Same(libraryGroup, Assert.Single(model.AssemblyGroups));
            Assert.All(model.ThirdPartyItems, item => Assert.Equal("Library content", item.Category));
            Assert.All(GetControlList(view, libraryGroup).Items.OfType<ToolboxPanelItem>(),
                item => Assert.Equal("Library content", item.Category));
            search.Text = "__no_such_toolbox_control__";
            FlushLayout(window);
            Assert.Empty(model.AssemblyGroups);
            Assert.Null(model.SelectedItem);
            Assert.True(Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == "No matching controls").IsVisible);
            Assert.False(Assert.Single(view.GetVisualDescendants().OfType<Avalonia.Controls.Button>(),
                button => Equals(button.Content, "Insert Selected")).IsEffectivelyEnabled);
            await model.ClearFiltersCommand.Execute().ToTask();

            FlushLayout(window);
            GetControlList(view, libraryGroup).SelectedItem = externalButton;
            Expander libraryExpander = GetExpander(view, libraryGroup);
            ScrollViewer viewport = Assert.IsType<ScrollViewer>(view.FindControl<ScrollViewer>("AssemblyGroupsViewport"));
            double previousHeight = libraryExpander.Bounds.Height;
            double previousExtent = viewport.Extent.Height;
            Assert.InRange(GetControlList(view, libraryGroup).Bounds.Height, 1, viewport.Bounds.Height + 1);
            libraryExpander.IsExpanded = false;
            FlushLayout(window);
            Assert.False(libraryGroup.IsExpanded);
            Assert.True(libraryExpander.Bounds.Height < previousHeight, "A collapsed assembly must not retain its control-list height.");
            Assert.True(viewport.Extent.Height < previousExtent, "Collapsing an assembly must release space in the toolbox.");
            Assert.Same(externalButton, model.SelectedItem);
            search.Text = secondLibraryName;
            FlushLayout(window);
            Assert.Same(secondLibraryGroup, Assert.Single(model.AssemblyGroups));
            await model.ClearFiltersCommand.Execute().ToTask();
            FlushLayout(window);
            Assert.Same(libraryGroup, Assert.Single(model.AssemblyGroups, group => group.AssemblyName == libraryName));
            Assert.False(GetExpander(view, libraryGroup).IsExpanded);

            GetControlList(view, secondLibraryGroup).SelectedItem = secondLibraryControl;
            await model.ReloadAsync();
            FlushLayout(window);
            Assert.False(GetExpander(view, libraryGroup).IsExpanded);
            Assert.Equal(secondLibraryControl.FullName, model.SelectedItem?.FullName);
            Assert.Same(model.SelectedItem, GetControlList(view, secondLibraryGroup).SelectedItem);

            Task refreshed = model.WhenAnyValue(item => item.IsLoading).Skip(1).Where(loading => !loading)
                .Take(1).Timeout(TimeSpan.FromSeconds(15)).ToTask();
            catalog.SetWorkspaceAssemblies(Array.Empty<string>());
            await refreshed;
            FlushLayout(window);
            Assert.DoesNotContain(model.AssemblyGroups, group => group.AssemblyName == libraryName || group.AssemblyName == secondLibraryName);
            Assert.NotEmpty(model.AssemblyGroups);
            Assert.All(model.Items, item => Assert.True(item.IsStandardControl));
            Assert.True(model.SelectedItem?.IsStandardControl);
            Assert.All(view.GetVisualDescendants().OfType<Expander>(), expander =>
                Assert.All(Assert.IsType<ToolboxAssemblyGroup>(expander.DataContext).Items, item => Assert.True(item.IsStandardControl)));
        }
        finally
        {
            window?.Close();
            application.Styles.Remove(theme);
        }
    }

    private static void FlushLayout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Expander GetExpander(ToolboxPanelView view, ToolboxAssemblyGroup group)
    {
        Expander[] matches = view.GetVisualDescendants().OfType<Expander>()
            .Where(expander => ReferenceEquals(expander.DataContext, group)).ToArray();
        Assert.True(matches.Length == 1, $"Expected one expander for '{group.AssemblyName}'. Items controls: " +
            string.Join("; ", view.GetVisualDescendants().OfType<ItemsControl>().Take(10)
                .Select(control => $"{control.GetType().Name}: {control.Items.Count} items, bounds {control.Bounds}, " +
                    $"theme {control.Theme?.TargetType?.Name ?? "none"}, template {control.Template?.GetType().Name ?? "none"}, variant {control.ActualThemeVariant}")));
        return matches[0];
    }

    private static ListBox GetControlList(ToolboxPanelView view, ToolboxAssemblyGroup group) =>
        Assert.Single(GetExpander(view, group).GetVisualDescendants().OfType<ListBox>());

    private static void AssertAlphabetical(ListBox list)
    {
        string[] names = list.Items.OfType<ToolboxPanelItem>().Select(item => item.DisplayName).ToArray();
        Assert.Equal(names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray(), names);
    }
}
