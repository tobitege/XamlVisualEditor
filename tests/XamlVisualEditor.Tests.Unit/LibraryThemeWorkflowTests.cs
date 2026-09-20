using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.TestControls;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class LibraryThemeWorkflowTests
{
    [AvaloniaFact]
    public async Task ApplicationThemePreservesResourcesConfigurationAndDocumentStylePriority()
    {
        const string applicationXaml = """
            <Application xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:library="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls">
              <Application.Resources>
                <SolidColorBrush x:Key="ApplicationAccentBrush" Color="#228866" />
              </Application.Resources>
              <Application.DataTemplates>
                <DataTemplate DataType="{x:Type x:String}">
                  <TextBlock Name="FromApplicationTemplate" Text="{Binding}"
                             Foreground="{StaticResource ApplicationAccentBrush}" />
                </DataTemplate>
              </Application.DataTemplates>
              <Application.Styles>
                <FluentTheme DensityStyle="Compact" />
                <Style Selector=":is(library|LibraryInput)">
                  <Setter Property="Foreground" Value="{StaticResource ApplicationAccentBrush}" />
                </Style>
              </Application.Styles>
            </Application>
            """;
        const string documentXaml = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:library="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls"
                    Width="240" Height="100">
              <!-- document-styles -->
              <StackPanel>
                <library:DerivedLibraryInput Text="Themed input" Width="120" MinHeight="21" />
                <ContentControl Name="TemplateSample" Content="Template payload" />
              </StackPanel>
            </Window>
            """;
        const string localStyle = """
            <Window.Styles>
              <Style Selector=":is(library|LibraryInput)">
                <Setter Property="Foreground" Value="Tomato" />
              </Style>
            </Window.Styles>
            <Window.DataTemplates>
              <DataTemplate DataType="{x:Type x:String}">
                <TextBlock Name="FromDocumentTemplate" Text="{Binding}" />
              </DataTemplate>
            </Window.DataTemplates>
            """;
        string appPath = Path.Combine(Path.GetTempPath(), "xve-app-theme-" + Guid.NewGuid().ToString("N") + ".axaml");
        await File.WriteAllTextAsync(appPath, applicationXaml);
        IReadOnlyList<Func<IStyle?>> previousStyles = WorkspaceDesignThemeRegistry.StyleFactories;
        IReadOnlyList<Func<IResourceProvider?>> previousResources = WorkspaceDesignThemeRegistry.ResourceFactories;
        try
        {
            TypeMetadataService metadata = new();
            metadata.LoadAssembly(typeof(Control).Assembly.Location);
            metadata.LoadAssembly(typeof(Brush).Assembly.Location);
            metadata.LoadAssembly(typeof(LibraryInput).Assembly.Location);
            WorkspaceDesignThemeLoader.LoadFromApplicationXaml(appPath);
            Styles scope = Assert.IsType<Styles>(Assert.Single(WorkspaceDesignThemeRegistry.StyleFactories)());
            FluentTheme fluent = Assert.Single(scope.OfType<Styles>().SelectMany(styles => styles).OfType<FluentTheme>());
            Assert.Equal("Compact", fluent.DensityStyle.ToString());

            for (int local = 0; local < 2; local++)
            {
                string path = Path.Combine(Path.GetTempPath(), "xve-theme-document-" + Guid.NewGuid().ToString("N") + ".axaml");
                await File.WriteAllTextAsync(path, documentXaml.Replace("<!-- document-styles -->",
                    local == 0 ? string.Empty : localStyle, StringComparison.Ordinal));
                using DesignerDocumentViewModel document = new(path, metadata);
                await document.LoadAsync();
                DesignSurfaceView view = new() { DataContext = document.DesignSurface };
                Window editor = new()
                {
                    Width = 500, Height = 300,
                    Content = new Border { DataContext = document, Child = view }
                };
                try
                {
                    editor.Show();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    editor.UpdateLayout();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    Assert.Empty(document.Diagnostics);
                    DerivedLibraryInput input = Assert.Single(document.DesignSurface.ControlMap.Keys.OfType<DerivedLibraryInput>());
                    Assert.NotNull(input.Template);
                    Assert.True(input.Bounds.Width > 0 && input.Bounds.Height >= 21);
                    Color expected = local == 0 ? Color.Parse("#228866") : Colors.Tomato;
                    Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(input.Foreground).Color);
                    ContentControl content = Assert.Single(document.DesignSurface.ControlMap.Keys
                        .OfType<ContentControl>(), control => control.Name == "TemplateSample");
                    string templateName = local == 0 ? "FromApplicationTemplate" : "FromDocumentTemplate";
                    TextBlock text = Assert.Single(content.GetVisualDescendants().OfType<TextBlock>()
                        , block => block.Name == templateName);
                    Assert.Equal("Template payload", text.Text);
                    if (local == 0)
                        Assert.Equal(Color.Parse("#228866"), Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
                }
                finally
                {
                    editor.Content = null;
                    editor.Close();
                }
            }
        }
        finally
        {
            WorkspaceDesignThemeRegistry.Update(previousStyles, previousResources);
        }
    }

    [AvaloniaFact]
    public void LibraryThemeRendersInheritedControlInsideEachEmbeddedWindow()
    {
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(LibraryInput).Assembly.Location);
        const string xaml = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:library="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls"
                    Width="240" Height="100">
                <library:DerivedLibraryInput Text="Project input" Width="120" MinHeight="21" />
            </Window>
            """;
        MutableAstDocument document = Assert.IsType<MutableAstDocument>(new XamlParsingService().Parse(xaml).Document);
        IStyle? previousStyle = null;
        for (int rebuild = 0; rebuild < 2; rebuild++)
        {
            RuntimeDesignRenderResult rendered = new RuntimeDesignRenderer(metadata).Render(document,
                new Uri(Path.Combine(Path.GetTempPath(), "library-theme-workflow.axaml")), typeof(LibraryInput).Assembly);
            Window root = Assert.IsType<Window>(rendered.Root);
            IStyle theme = Assert.Single(LibraryDesignThemeLoader.CreateStyles(root, metadata, _ => { }));
            Assert.NotSame(previousStyle, theme);
            previousStyle = theme;
            root.Styles.Add(theme);
            using DesignWindowContentHost surface = new(root);
            Window editor = new() { Width = 400, Height = 200, Content = surface };
            try
            {
                editor.Show();
                editor.UpdateLayout();
                DerivedLibraryInput input = Assert.IsType<DerivedLibraryInput>(root.Content);
                Assert.NotNull(input.Template);
                Assert.True(input.Bounds.Width > 0 && input.Bounds.Height >= 21);
                Assert.Contains(input.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Project input");
                Border border = Assert.Single(input.GetVisualDescendants().OfType<Border>());
                Assert.Equal(Colors.CornflowerBlue, Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color);
                Assert.Empty(rendered.Diagnostics);
            }
            finally
            {
                editor.Content = null;
                editor.Close();
            }
        }
    }
}
