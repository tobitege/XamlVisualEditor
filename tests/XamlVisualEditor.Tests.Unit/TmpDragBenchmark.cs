using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class TmpDragBenchmark
{
    [AvaloniaFact(Timeout = 120000)]
    public async Task Drag_with_30_controls()
    {
        StringBuilder xaml = new();
        xaml.AppendLine("<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Width=\"800\" Height=\"600\">");
        xaml.AppendLine("  <Canvas>");
        for (int i = 0; i < 30; i++)
        {
            int col = i % 6;
            int row = i / 6;
            xaml.AppendLine($"    <Button Canvas.Left=\"{20 + col * 120}\" Canvas.Top=\"{20 + row * 100}\" Width=\"90\" Height=\"32\" Content=\"B{i}\" />");
        }
        xaml.AppendLine("  </Canvas>");
        xaml.AppendLine("</UserControl>");
        string path = Path.Combine(Path.GetTempPath(), "xve-drag-" + Guid.NewGuid().ToString("N") + ".axaml");
        await File.WriteAllTextAsync(path, xaml.ToString());
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        using DesignerDocumentViewModel document = new(path, metadata);
        await document.LoadAsync();
        DesignSurfaceView view = new() { DataContext = document.DesignSurface };
        Border documentHost = new() { DataContext = document, Child = view };
        Window host = new() { Content = documentHost, Width = 1200, Height = 900 };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await FinishLayoutAsync(host);
            Assert.Equal(32, document.DesignSurface.ItemMap.Count);
            DesignItem target = document.DesignSurface.ItemMap.Values.First(item => item.AstNode.GetPropertyValue("Content") == "B0");
            Control visual = target.VisualElement!;
            Point start = visual.TranslatePoint(new Point(10, 10), host)!.Value;
            host.MouseDown(start, MouseButton.Left);
            await FinishLayoutAsync(host);
            Assert.Contains(target, document.DesignSurface.Selection.SelectedItems);

            int moves = 200;
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 1; i <= moves; i++)
            {
                host.MouseMove(start + new Vector(i * 1.5, i * 0.7));
                host.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            sw.Stop();
            double perMove = sw.Elapsed.TotalMilliseconds / moves;
            int rebuilds = 0;
            document.DesignSurface.RebuildRequested += () => rebuilds++;
            int syncs = 0;
            document.SyncEngine.SyncCompleted += _ => syncs++;
            Stopwatch drop = Stopwatch.StartNew();
            host.MouseUp(start + new Vector(moves * 1.5, moves * 0.7), MouseButton.Left);
            double afterRelease = drop.Elapsed.TotalMilliseconds;
            await FinishLayoutAsync(host);
            for (int i = 0; i < 20 && document.DesignSurface.IsRebuilding; i++) await FinishLayoutAsync(host);
            drop.Stop();
            bool sameVisual = ReferenceEquals(visual, document.DesignSurface.ItemMap.Values.First(item => item.AstNode.GetPropertyValue("Content") == "B0").VisualElement);
            Stopwatch rebuild = Stopwatch.StartNew();
            document.DesignSurface.RequestRebuild();
            for (int i = 0; i < 50 && document.DesignSurface.IsRebuilding; i++) await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            rebuild.Stop();
            double left = double.Parse(target.AstNode.GetPropertyValue("Canvas.Left")!, System.Globalization.CultureInfo.InvariantCulture);
            int visuals = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(host).Count();
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "xve-drag-bench.txt"),
                $"moves={moves} total={sw.Elapsed.TotalMilliseconds:0}ms perMove={perMove:0.00}ms finalLeft={left} visuals={visuals} " +
                $"release={afterRelease:0}ms dropTotal={drop.Elapsed.TotalMilliseconds:0}ms rebuilds={rebuilds} syncs={syncs} sameVisual={sameVisual} rebuildOnly={rebuild.Elapsed.TotalMilliseconds:0}ms");
            Assert.True(left > 20);
        }
        finally { host.Close(); }
    }

    private static async Task FinishLayoutAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
