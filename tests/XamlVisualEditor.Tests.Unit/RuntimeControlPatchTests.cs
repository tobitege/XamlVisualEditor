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
    public void LayoutUpdatesAndResetsBypassXamlPopulationForEveryLiveInstance()
    {
        Guid nodeId = Guid.NewGuid();
        Guid revision = Guid.NewGuid();
        string Source(Guid id, string attributes) =>
            $"<Canvas xmlns='https://github.com/avaloniaui' xmlns:id='{DesignerUpdateProtocol.IdentityNamespace}' id:DesignNodeIdentity.RenderId='{id}'>"
            + $"<Button id:DesignNodeIdentity.NodeId='{nodeId}' {attributes} /></Canvas>";
        Button first = new();
        Button second = new();
        first.SetValue(Control.WidthProperty, 60d, Avalonia.Data.BindingPriority.Style);
        second.SetValue(Control.WidthProperty, 70d, Avalonia.Data.BindingPriority.Style);
        int populations = 0;
        IncrementalPreviewUpdater updater = new();
        updater.Record(Source(revision, ""), revision, (_, _) => populations++);
        Guid changed = Guid.NewGuid();
        DesignerUpdateResult update = updater.Apply(new(Guid.NewGuid(), revision, changed,
            Source(changed, "Width='80' Height='35' Canvas.Left='25' Margin='1,2,3,4' Grid.Column='2'")),
            _ => new object[] { first, second }, _ => { });
        Assert.True(update.Applied, update.Error);
        foreach (Button button in new[] { first, second })
        {
            Assert.Equal(80, button.Width);
            Assert.Equal(35, button.Height);
            Assert.Equal(25, Canvas.GetLeft(button));
            Assert.Equal(new Thickness(1, 2, 3, 4), button.Margin);
            Assert.Equal(2, Grid.GetColumn(button));
        }
        Guid cleared = Guid.NewGuid();
        DesignerUpdateResult reset = updater.Apply(new(Guid.NewGuid(), changed, cleared, Source(cleared, "")),
            _ => new object[] { first, second }, _ => { });
        Assert.True(reset.Applied, reset.Error);
        Assert.Equal(60, first.Width);
        Assert.Equal(70, second.Width);
        Assert.False(first.IsSet(Canvas.LeftProperty));
        Assert.False(second.IsSet(Grid.ColumnProperty));
        Assert.Equal(0, populations);
    }

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
