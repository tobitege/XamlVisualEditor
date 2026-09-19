using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
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
