using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using XamlVisualEditor.Designer.Rendering;
using LibraryGrid = XamlVisualEditor.TestControls.LibraryGrid;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class RuntimeRenderingWorkflowTests
{
    [AvaloniaFact]
    public void Foreign_container_resources_template_and_binding_render_with_source_identities()
    {
        const string xaml = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:library="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls"
                         x:CompileBindings="False" Width="480" Height="260">
              <UserControl.Resources>
                <SolidColorBrush x:Key="Accent" Color="Tomato" />
              </UserControl.Resources>
              <UserControl.Styles>
                <Style Selector="Button"><Setter Property="Padding" Value="7" /></Style>
              </UserControl.Styles>
              <library:LibraryGrid RowDefinitions="Auto,*" ColumnDefinitions="2*,*">
                <Button x:Name="Action" Grid.Column="1" Content="Run" Background="{StaticResource Accent}">
                  <Button.Template>
                    <ControlTemplate>
                      <Border Name="PART_Frame" Background="{TemplateBinding Background}">
                        <TextBlock Text="{TemplateBinding Content}" />
                      </Border>
                    </ControlTemplate>
                  </Button.Template>
                </Button>
                <TextBox x:Name="Caption" Grid.Row="1" Text="{Binding Caption}" />
              </library:LibraryGrid>
            </UserControl>
            """;
        var parsed = new XamlParsingService().Parse(xaml);
        Assert.False(parsed.HasErrors);
        MutableAstDocument document = Assert.IsType<MutableAstDocument>(parsed.Document);
        XamlSerializationService serializer = new();
        string original = serializer.Serialize(document);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        metadata.LoadAssembly(typeof(LibraryGrid).Assembly.Location);

        RuntimeDesignRenderResult rendered = new RuntimeDesignRenderer(metadata).Render(document,
            new Uri("avares://XamlVisualEditor.TestControls/DesignerFixture.axaml"), typeof(LibraryGrid).Assembly);
        UserControl root = Assert.IsType<UserControl>(rendered.Root);
        root.DataContext = new PreviewData { Caption = "Bound value" };
        Window host = new() { Content = root, Width = 500, Height = 300 };
        try
        {
            host.Show();
            host.UpdateLayout();
            LibraryGrid grid = Assert.IsType<LibraryGrid>(root.Content);
            Assert.Equal(2, grid.RowDefinitions.Count);
            Assert.Equal(new GridLength(2, GridUnitType.Star), grid.ColumnDefinitions[0].Width);
            Button button = Assert.IsType<Button>(grid.Children[0]);
            Assert.Equal("Action", button.Name);
            Assert.Equal(1, Grid.GetColumn(button));
            Assert.Equal(new Thickness(7), button.Padding);
            Assert.Equal(Colors.Tomato, Assert.IsType<SolidColorBrush>(button.Background).Color);
            Assert.Equal("Bound value", Assert.IsType<TextBox>(grid.Children[1]).Text);
            Border frame = Assert.Single(button.GetVisualDescendants().OfType<Border>(), candidate => candidate.Name == "PART_Frame");

            IReadOnlyDictionary<Control, Guid> identities = rendered.GetControlNodeIds();
            MutableAstObjectNode[] nodes = Objects(document.Root!).ToArray();
            Assert.Equal(document.Root!.Id, identities[root]);
            Assert.Equal(Assert.Single(nodes, node => node.TypeName == nameof(LibraryGrid)).Id, identities[grid]);
            Assert.Equal(Assert.Single(nodes, node => node.TypeName == "Button").Id, identities[button]);
            Assert.Equal(Assert.Single(nodes, node => node.TypeName == "Border").Id, identities[frame]);
            Assert.Equal(original, serializer.Serialize(document));
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public void Embedded_window_preserves_ancestor_namescope_and_live_resource_bindings()
    {
        const string xaml = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Name="PreviewWindow" x:CompileBindings="False" Title="Before" Width="420" Height="220">
              <Window.Resources><SolidColorBrush x:Key="Accent" Color="Tomato" /></Window.Resources>
              <StackPanel>
                <TextBlock Text="{Binding $parent[Window].Title}" />
                <TextBlock Text="{Binding #PreviewWindow.Title}" />
                <Border Height="20" Background="{DynamicResource Accent}" />
              </StackPanel>
            </Window>
            """;
        var parsed = new XamlParsingService().Parse(xaml);
        Assert.False(parsed.HasErrors);
        MutableAstDocument document = Assert.IsType<MutableAstDocument>(parsed.Document);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        RuntimeDesignRenderResult rendered = new RuntimeDesignRenderer(metadata).Render(document,
            new Uri("avares://XamlVisualEditor.Tests.Unit/WindowFixture.axaml"), typeof(Control).Assembly);
        Window sourceWindow = Assert.IsType<Window>(rendered.Root);
        StackPanel content = Assert.IsType<StackPanel>(sourceWindow.Content);
        bool closed = false;
        sourceWindow.Closed += (_, _) => closed = true;
        using DesignWindowContentHost surface = new(sourceWindow);
        Window host = new() { Content = surface, Width = 500, Height = 300 };
        try
        {
            host.Show();
            host.UpdateLayout();
            Assert.Same(sourceWindow, content.Parent);
            TextBlock ancestorBinding = Assert.IsType<TextBlock>(content.Children[0]);
            TextBlock namedBinding = Assert.IsType<TextBlock>(content.Children[1]);
            Border resourceBinding = Assert.IsType<Border>(content.Children[2]);
            Assert.Equal("Before", ancestorBinding.Text);
            Assert.Equal("Before", namedBinding.Text);
            Assert.Equal(Colors.Tomato, Assert.IsType<SolidColorBrush>(resourceBinding.Background).Color);

            sourceWindow.Title = "After";
            sourceWindow.Width = 460;
            sourceWindow.Resources["Accent"] = new SolidColorBrush(Colors.CornflowerBlue);
            host.UpdateLayout();
            Assert.Equal("After", ancestorBinding.Text);
            Assert.Equal("After", namedBinding.Text);
            Assert.Equal(460, surface.Width);
            Assert.Equal(Colors.CornflowerBlue, Assert.IsType<SolidColorBrush>(resourceBinding.Background).Color);
            IReadOnlyDictionary<Control, Guid> identities = rendered.GetControlNodeIds(surface);
            Assert.Equal(document.Root!.Id, identities[surface]);
            Assert.DoesNotContain(sourceWindow, identities.Keys);
            Assert.Same(content, sourceWindow.Content);
        }
        finally { host.Close(); }
        Assert.True(closed);
    }

    [AvaloniaFact]
    public void Failed_library_constructor_keeps_valid_siblings_rendered_and_mapped()
    {
        const string xaml = """
            <StackPanel xmlns="https://github.com/avaloniaui"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:library="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls">
              <Button x:Name="Good" Content="Still editable" />
              <library:ConstructorTrapControl x:Name="Bad" Width="180" Height="70" />
              <TextBlock Text="After the failing control" />
            </StackPanel>
            """;
        var parsed = new XamlParsingService().Parse(xaml);
        Assert.False(parsed.HasErrors);
        MutableAstDocument document = Assert.IsType<MutableAstDocument>(parsed.Document);
        XamlSerializationService serializer = new();
        string original = serializer.Serialize(document);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        metadata.LoadAssembly(typeof(LibraryGrid).Assembly.Location);
        RuntimeDesignRenderResult rendered = new RuntimeDesignRenderer(metadata).Render(document,
            new Uri("avares://XamlVisualEditor.TestControls/FailingFixture.axaml"), typeof(LibraryGrid).Assembly);
        StackPanel root = Assert.IsType<StackPanel>(rendered.Root);
        Window host = new() { Content = root, Width = 500, Height = 300 };
        try
        {
            host.Show();
            host.UpdateLayout();
            Button good = Assert.IsType<Button>(root.Children[0]);
            DesignErrorControl bad = Assert.IsType<DesignErrorControl>(root.Children[1]);
            Assert.Equal("Still editable", good.Content);
            Assert.Contains("ConstructorTrapControl", bad.OriginalType, StringComparison.Ordinal);
            Assert.Contains("Catalog discovery must not construct controls", bad.Error, StringComparison.Ordinal);
            Assert.Equal(180, bad.Width);
            Assert.Equal("After the failing control", Assert.IsType<TextBlock>(root.Children[2]).Text);
            IReadOnlyDictionary<Control, Guid> identities = rendered.GetControlNodeIds();
            Assert.Equal(document.Root!.Children[0].Id, identities[good]);
            Assert.Equal(document.Root.Children[1].Id, identities[bad]);
            Assert.Equal(document.Root.Children[1].Id, Assert.Single(rendered.Diagnostics).NodeId);
            Assert.Equal(original, serializer.Serialize(document));
        }
        finally { host.Close(); }
    }

    private static IEnumerable<MutableAstObjectNode> Objects(MutableAstNode node)
    {
        if (node is MutableAstPropertyNode property && property.Value is { } value)
        {
            foreach (MutableAstObjectNode child in Objects(value)) yield return child;
        }
        if (node is not MutableAstObjectNode element) yield break;
        yield return element;
        foreach (MutableAstNode child in element.Properties.Cast<MutableAstNode>().Concat(element.Children))
            foreach (MutableAstObjectNode descendant in Objects(child)) yield return descendant;
    }

    public sealed class PreviewData
    {
        public string Caption { get; init; } = string.Empty;
    }
}
