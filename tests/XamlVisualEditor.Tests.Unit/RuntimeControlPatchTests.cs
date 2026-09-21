using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.PreviewerHost;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class RuntimeControlPatchTests
{
    [AvaloniaFact]
    public void AddingAndResettingPropertiesKeepsControlsAndRestoresStyledValues()
    {
        Guid nodeId = Guid.NewGuid();
        Guid revision = Guid.NewGuid();
        string Source(Guid id, string attributes) =>
            $"<Canvas xmlns='https://github.com/avaloniaui' xmlns:id='{DesignerUpdateProtocol.IdentityNamespace}' id:DesignNodeIdentity.RenderId='{id}'>"
            + $"<lib:CaptionControl xmlns:lib='clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls' id:DesignNodeIdentity.NodeId='{nodeId}' {attributes} /></Canvas>";
        XamlVisualEditor.TestControls.CaptionControl button = new();
        Button sibling = new();
        Canvas canvas = new() { Children = { button, sibling } };
        button.SetValue(ContentControl.WidthProperty, 60, Avalonia.Data.BindingPriority.Style);
        canvas.Resources["Accent"] = Brushes.Coral;
        Window window = new() { Content = canvas };
        IncrementalPreviewUpdater updater = new();
        updater.Record(Source(revision, ""), revision, (control, fragment) =>
            AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(control, fragment),
                new RuntimeXamlLoaderConfiguration { DesignMode = true }));
        try
        {
            window.Show();
            Guid next = Guid.NewGuid();
            var added = updater.Apply(new(Guid.NewGuid(), revision, next,
                Source(next, "Width='80' Canvas.Left='25' Background='{DynamicResource Accent}'")),
                _ => new object[] { button }, _ => window.UpdateLayout());
            Assert.True(added.Applied, added.Error);
            Assert.Equal(80, button.Width);
            Assert.Equal(25, Canvas.GetLeft(button));
            Assert.Equal(Colors.Coral, Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
            Guid resetRevision = Guid.NewGuid();
            var removed = updater.Apply(new(Guid.NewGuid(), next, resetRevision, Source(resetRevision, "")),
                _ => new object[] { button }, _ => window.UpdateLayout());
            Assert.True(removed.Applied, removed.Error);
            Assert.Equal(60, button.Width);
            Assert.False(button.IsSet(Canvas.LeftProperty));
            Assert.False(button.IsSet(ContentControl.BackgroundProperty));
            Assert.Same(button, canvas.Children[0]);
            Assert.Same(sibling, canvas.Children[1]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RuntimePopulationKeepsExistingControlsAndResolvesTheirResources()
    {
        UserControl root = AvaloniaRuntimeXamlLoader.Parse<UserControl>("""
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:CompileBindings="False">
              <UserControl.Resources><SolidColorBrush x:Key="Accent" Color="Coral" /></UserControl.Resources>
              <Canvas><Button Name="Target" Content="Original" Canvas.Left="10" Width="40" /><TextBlock Text="{Binding #Target.Content}" /></Canvas>
            </UserControl>
            """);
        Canvas canvas = Assert.IsType<Canvas>(root.Content);
        Button button = Assert.IsType<Button>(canvas.Children[0]);
        TextBlock sibling = Assert.IsType<TextBlock>(canvas.Children[1]);
        Window window = new() { Content = root };
        try
        {
            window.Show();
            object result = AvaloniaRuntimeXamlLoader.Load(new RuntimeXamlLoaderDocument(button, """
                <Button xmlns="https://github.com/avaloniaui" Width="80" Canvas.Left="25" Content="Updated" Background="{DynamicResource Accent}" />
                """), new RuntimeXamlLoaderConfiguration { DesignMode = true });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Same(button, result);
            Assert.Same(button, canvas.Children[0]);
            Assert.Same(sibling, canvas.Children[1]);
            Assert.Equal(80, button.Width);
            Assert.Equal(25, Canvas.GetLeft(button));
            Assert.Equal("Updated", sibling.Text);
            Assert.Equal(Colors.Coral, Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
        }
        finally { window.Close(); }
    }
}
