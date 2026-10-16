using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using XamlVisualEditor.Designer.PreviewerHost;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignTreeReporterTests
{
    [Fact]
    public void Read_PrefersTheMostDerivedDeclarationOfAHiddenProperty()
    {
        HidingControl control = new() { Padding = "derived" };
        ((BaseControl)control).Padding = 4;

        Assert.Equal("derived", DesignTreeReporter.Read(control, "Padding"));
    }

    [Fact]
    public void Read_SelectsTheHiddenDeclarationByPropertyType()
    {
        HidingControl control = new() { Padding = "derived" };
        ((BaseControl)control).Padding = 4;

        Assert.Equal(4d, DesignTreeReporter.Read(control, "Padding", typeof(double).FullName));
    }

    [Fact]
    public void Read_ReturnsNullForAMissingPropertyOrType()
    {
        HidingControl control = new();

        Assert.Null(DesignTreeReporter.Read(control, "Missing"));
        Assert.Null(DesignTreeReporter.Read(control, "Padding", "Avalonia.Thickness"));
    }

    [AvaloniaFact]
    public void Publish_ReportsClipsVisibilityAndStableInstanceIds()
    {
        PreviewTree tree = new();
        Window window = tree.Show();
        try
        {
            List<string> lines = new();
            DesignTreeReporter.Track(tree.Root, lines.Add);
            DesignTreeReporter.Refresh(tree.RenderId);
            DesignerHostSnapshot first = Snapshot(Assert.Single(lines));

            Assert.Equal(tree.RenderId, first.RenderId);
            DesignerHostNode root = Node(first, tree.RootId);
            DesignerHostNode clipped = Node(first, tree.ClippedId);
            DesignerHostNode hidden = Node(first, tree.HiddenId);
            DesignerHostNode outside = Node(first, tree.OutsideId);
            DesignerHostNode nested = Node(first, tree.NestedId);
            Assert.Null(root.ParentInstanceId);
            Assert.All(new[] { clipped, hidden, outside }, node => Assert.Equal(root.InstanceId, node.ParentInstanceId));
            Assert.Equal(outside.InstanceId, nested.ParentInstanceId);
            Assert.Equal(first.Nodes.Count, first.Nodes.Select(node => node.InstanceId).Distinct().Count());
            Assert.All(first.Nodes, node => Assert.True(node.InstanceId > 0));

            // The root clips its children to its own bounds; a child keeps its full size and inherits that clip.
            Assert.Equal(new DesignerHostRect(root.X, root.Y, 100, 80), root.Clip);
            Assert.Equal(root.Clip, clipped.Clip);
            Assert.Equal(root.X + 80, clipped.X);
            Assert.Equal(50, clipped.Width);
            Assert.Equal(20, clipped.Clip!.X + clipped.Clip.Width - clipped.X);
            Assert.True(root.IsVisible);
            Assert.True(clipped.IsVisible);
            Assert.False(hidden.IsVisible);
            Assert.Equal(0, outside.Clip!.Width);
            Assert.False(outside.IsVisible);
            Assert.False(nested.IsVisible);

            lines.Clear();
            tree.Clipped.Width = 60;
            Guid next = Guid.NewGuid();
            DesignTreeReporter.Refresh(next);
            DesignerHostSnapshot second = Snapshot(Assert.Single(lines));

            Assert.Equal(next, second.RenderId);
            Assert.Equal(60, Node(second, tree.ClippedId).Width);
            Assert.Equal(first.Nodes.Select(node => (node.NodeId, node.InstanceId)),
                second.Nodes.Select(node => (node.NodeId, node.InstanceId)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Publish_CoalescesLayoutPassesOfOneDispatcherPass()
    {
        PreviewTree tree = new();
        Window window = tree.Show();
        try
        {
            List<string> lines = new();
            DesignTreeReporter.Track(tree.Root, lines.Add);
            Dispatcher.UIThread.RunJobs();
            lines.Clear();

            tree.Clipped.Width = 60;
            tree.Root.UpdateLayout();
            tree.Clipped.Width = 70;
            tree.Root.UpdateLayout();
            Assert.Empty(lines);

            Dispatcher.UIThread.RunJobs();
            Assert.Equal(70, Node(Snapshot(Assert.Single(lines)), tree.ClippedId).Width);
        }
        finally
        {
            window.Close();
        }
    }

    private static DesignerHostSnapshot Snapshot(string line)
    {
        Assert.StartsWith(DesignTreeReporter.MessagePrefix, line, StringComparison.Ordinal);
        return Assert.IsType<DesignerHostSnapshot>(JsonSerializer.Deserialize<DesignerHostSnapshot>(
            line.AsSpan(DesignTreeReporter.MessagePrefix.Length)));
    }

    private static DesignerHostNode Node(DesignerHostSnapshot snapshot, Guid nodeId) =>
        Assert.Single(snapshot.Nodes, node => node.NodeId == nodeId);

    /// <summary>A clipping canvas with a partly clipped, a hidden and a fully clipped child.</summary>
    private sealed class PreviewTree
    {
        public Guid RenderId { get; } = Guid.NewGuid();
        public Guid RootId { get; } = Guid.NewGuid();
        public Guid ClippedId { get; } = Guid.NewGuid();
        public Guid HiddenId { get; } = Guid.NewGuid();
        public Guid OutsideId { get; } = Guid.NewGuid();
        public Guid NestedId { get; } = Guid.NewGuid();
        public Canvas Root { get; }
        public Border Clipped { get; } = new() { Width = 50, Height = 20 };

        public PreviewTree()
        {
            Root = new Canvas
            {
                Width = 100, Height = 80, ClipToBounds = true,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };
            DesignNodeIdentity.SetRenderId(Root, RenderId.ToString());
            DesignNodeIdentity.SetNodeId(Root, RootId.ToString());
            Canvas.SetLeft(Clipped, 80);
            Canvas.SetTop(Clipped, 10);
            DesignNodeIdentity.SetNodeId(Clipped, ClippedId.ToString());
            Border hidden = new() { Width = 10, Height = 10, IsVisible = false };
            DesignNodeIdentity.SetNodeId(hidden, HiddenId.ToString());
            Canvas outside = new() { Width = 10, Height = 10, ClipToBounds = true };
            Canvas.SetLeft(outside, 150);
            DesignNodeIdentity.SetNodeId(outside, OutsideId.ToString());
            Border nested = new() { Width = 5, Height = 5 };
            DesignNodeIdentity.SetNodeId(nested, NestedId.ToString());
            outside.Children.Add(nested);
            Root.Children.Add(Clipped);
            Root.Children.Add(hidden);
            Root.Children.Add(outside);
        }

        public Window Show()
        {
            Panel host = new();
            host.Children.Add(Root);
            Window window = new() { Width = 300, Height = 200, Template = new FuncControlTemplate((_, _) => host) };
            window.Show();
            return window;
        }
    }

    private class BaseControl
    {
        public double Padding { get; set; }
    }

    private sealed class HidingControl : BaseControl
    {
        public new string? Padding { get; set; }
    }
}
