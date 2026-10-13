using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignSurfaceRenderingTests
{
    [AvaloniaTheory(Timeout = 30000)]
    [InlineData("MinWidth=\"300\" MinHeight=\"200\"", 1200, 700)]
    [InlineData("MinWidth=\"1500\" MinHeight=\"900\"", 1500, 900)]
    [InlineData("Width=\"480\" Height=\"260\"", 480, 260)]
    public async Task Artboard_grows_to_rendered_content_unless_the_document_has_an_explicit_size(
        string rootSize, double expectedWidth, double expectedHeight)
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string xaml = $"""
            <UserControl xmlns="https://github.com/avaloniaui" {rootSize}>
              <Border Width="1200" Height="700" Background="Tomato" />
            </UserControl>
            """;

        await RenderAsync(xaml, document =>
        {
            Assert.Equal(expectedWidth, document.DesignSurface.CanvasWidth);
            Assert.Equal(expectedHeight, document.DesignSurface.CanvasHeight);
        });
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Preview_hooks_receive_the_rendered_window_and_its_source_root()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string className = "Sample.HookProbe" + Guid.NewGuid().ToString("N");
        string xaml = $"""
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="{className}">
              <Border Name="Body" />
            </Window>
            """;
        RecordingHook hook = new(className);
        FailingHook failing = new();

        using (DesignPreviewHookRegistry.Register(failing))
        using (DesignPreviewHookRegistry.Register(hook))
        {
            await RenderAsync(xaml, _ => { });
        }

        (Control root, MutableAstObjectNode node, object? content) = Assert.Single(hook.Calls);
        Assert.IsAssignableFrom<Window>(root);
        Assert.Equal("Body", Assert.IsType<Border>(content).Name);
        Assert.Equal("Window", node.TypeName);
        Assert.DoesNotContain(hook, DesignPreviewHookRegistry.Hooks);
        Assert.DoesNotContain(failing, DesignPreviewHookRegistry.Hooks);
    }

    private static async Task RenderAsync(string xaml, Action<DesignerDocumentViewModel> verify)
    {
        string path = Path.Combine(Path.GetTempPath(), "xve-render-" + Guid.NewGuid().ToString("N") + ".axaml");
        await File.WriteAllTextAsync(path, xaml);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        using DesignerDocumentViewModel document = new(path, metadata);
        await document.LoadAsync();
        DesignSurfaceView view = new() { DataContext = document.DesignSurface };
        Window host = new() { Content = new Border { DataContext = document, Child = view }, Width = 800, Height = 600 };
        try
        {
            host.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            host.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            verify(document);
        }
        finally
        {
            host.Close();
            File.Delete(path);
        }
    }

    private sealed class RecordingHook(string className) : IDesignPreviewHook
    {
        // The window content is captured at hook time: closing the surface detaches it later.
        public List<(Control Root, MutableAstObjectNode Node, object? Content)> Calls { get; } = new();

        public void PostProcess(Control root, MutableAstObjectNode rootNode)
        {
            if (rootNode.Properties.Any(property => property.PropertyName == "x:Class"
                && property.Value is MutableAstTextNode { Text: var text } && text == className))
            {
                Calls.Add((root, rootNode, (root as ContentControl)?.Content));
            }
        }
    }

    private sealed class FailingHook : IDesignPreviewHook
    {
        public void PostProcess(Control root, MutableAstObjectNode rootNode) =>
            throw new InvalidOperationException("Hook failure must not stop the preview or other hooks.");
    }
}
