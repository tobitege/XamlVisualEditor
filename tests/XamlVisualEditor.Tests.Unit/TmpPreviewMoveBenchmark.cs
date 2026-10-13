using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class TmpPreviewMoveBenchmark
{
    [AvaloniaFact(Timeout = 120000)]
    public async Task PreviewMove_with_30_controls()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        StringBuilder xaml = new();
        xaml.Append("<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Width=\"800\" Height=\"600\"><Canvas>");
        for (int i = 0; i < 30; i++)
        {
            int col = i % 6;
            int row = i / 6;
            xaml.Append($"<Button Canvas.Left=\"{20 + col * 120}\" Canvas.Top=\"{20 + row * 100}\" Width=\"90\" Height=\"32\" Content=\"B{i}\" />");
        }
        xaml.Append("</Canvas></UserControl>");
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        metadata.LoadAssembly(typeof(Avalonia.Themes.Fluent.FluentTheme).Assembly.Location);
        using DesignerDocumentViewModel document = new("Bench.axaml", metadata);
        await document.SyncEngine.LoadAsync(xaml.ToString()).WaitAsync(cancellationToken);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode canvas = root.Children.OfType<MutableAstObjectNode>().First();
        MutableAstObjectNode[] buttons = canvas.Children.OfType<MutableAstObjectNode>().ToArray();
        List<DesignerHostNode> nodes = new()
        {
            new DesignerHostNode(root.Id, 1, null, "UserControl", null, 0, 0, 800, 600, true, Layout: new() { Kind = "Content", Width = 800, Height = 600 }),
            new DesignerHostNode(canvas.Id, 2, 1, "Canvas", null, 0, 0, 800, 600, true, Layout: new() { Kind = "Canvas", Width = 800, Height = 600 })
        };
        for (int i = 0; i < buttons.Length; i++)
        {
            int col = i % 6;
            int row = i / 6;
            double left = 20 + col * 120;
            double top = 20 + row * 100;
            nodes.Add(new DesignerHostNode(buttons[i].Id, 3 + i, 2, "Button", null, left, top, 90, 32, true,
                Layout: new() { X = left, Y = top, Width = 90, Height = 32, CanvasLeft = left, CanvasTop = top, M31 = left, M32 = top }));
        }
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 800, 600, nodes);
        DesignItem[] selection = { new(buttons[0]) { HostInstanceId = 3 } };

        PreviewerGeometryEditor editor = new(document, tree);
        // warm up
        editor.PreviewMove(selection, 3, new Vector(30, 30), new Point(60, 60));

        int iterations = 300;
        Stopwatch empty = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            new PreviewerGeometryEditor(document, tree).PreviewMove(selection, 3, new Vector(5 + i * 0.5, 40), new Point(65 + i * 0.5, 76));
        }
        empty.Stop();

        Stopwatch overSibling = Stopwatch.StartNew();
        PreviewerMovePreview? last = null;
        for (int i = 0; i < iterations; i++)
        {
            // pointer over B7 (140,120)-(230,152)
            last = new PreviewerGeometryEditor(document, tree).PreviewMove(selection, 3, new Vector(120 + i * 0.01, 100), new Point(165, 136));
        }
        overSibling.Stop();

        // snapping check: move B0 so left = 137 -> expect snap to 140 (B1 left)
        PreviewerMovePreview snap = new PreviewerGeometryEditor(document, tree).PreviewMove(selection, 3, new Vector(117, 0), new Point(65, 36));

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "xve-previewmove-bench.txt"),
            $"emptyPerCall={empty.Elapsed.TotalMilliseconds / iterations:0.000}ms overSiblingPerCall={overSibling.Elapsed.TotalMilliseconds / iterations:0.000}ms " +
            $"overSiblingTarget={last?.TargetBounds} overSiblingError={last?.Error} overSiblingSnapLines={last?.SnapLines?.Count} " +
            $"snapDelta={snap.Delta} snapLines={snap.SnapLines?.Count}");
    }
}
