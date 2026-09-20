using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

namespace XamlVisualEditor.Designer.PreviewerHost;

/// <summary>Reports source-identified controls without binding the host to the editor's Avalonia version.</summary>
internal sealed class DesignTreeReporter
{
    private const string MessagePrefix = "XVE-DESIGN-TREE ";
    private static DesignTreeReporter? s_current;
    private readonly object _root;
    private readonly Guid _renderId;
    private readonly MethodInfo _getNodeId;
    private readonly MethodInfo _transformToVisual;
    private readonly EventInfo _layoutUpdated;
    private readonly SynchronizationContext? _context;
    private readonly ConditionalWeakTable<object, InstanceIdentity> _instances = new();
    private int _nextInstanceId;
    private bool _queued;
    private string? _lastPayload;
    private string? _lastError;

    private DesignTreeReporter(object root, Guid renderId, MethodInfo getNodeId, EventInfo layoutUpdated)
    {
        _root = root;
        _renderId = renderId;
        _getNodeId = getNodeId;
        _layoutUpdated = layoutUpdated;
        _context = SynchronizationContext.Current;
        Type extensions = Type.GetType("Avalonia.VisualExtensions, Avalonia.Base", throwOnError: true)!;
        _transformToVisual = extensions.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "TransformToVisual" && method.GetParameters().Length == 2);
        _layoutUpdated.AddEventHandler(root, new EventHandler(OnLayoutUpdated));
    }

    public static void Track(object root)
    {
        Type? identity = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == "XamlVisualEditor.Designer.Rendering")
            ?.GetType("XamlVisualEditor.Designer.Rendering.DesignNodeIdentity");
        MethodInfo? getRenderId = identity?.GetMethod("GetRenderId");
        if (getRenderId is null || !getRenderId.GetParameters()[0].ParameterType.IsInstanceOfType(root)
            || !Guid.TryParse(getRenderId.Invoke(null, new[] { root }) as string, out Guid renderId)) return;
        EventInfo? layoutUpdated = root.GetType().GetEvent("LayoutUpdated");
        if (layoutUpdated is null) return;
        if (s_current is { } previous)
            previous._layoutUpdated.RemoveEventHandler(previous._root, new EventHandler(previous.OnLayoutUpdated));
        s_current = new DesignTreeReporter(root, renderId, identity!.GetMethod("GetNodeId")!, layoutUpdated);
    }

    private void OnLayoutUpdated(object? sender, EventArgs args)
    {
        if (_queued || !ReferenceEquals(s_current, this)) return;
        _queued = true;
        if (_context is null) Publish();
        else _context.Post(_ => Publish(), null);
    }

    private void Publish()
    {
        _queued = false;
        if (!ReferenceEquals(s_current, this)) return;
        try
        {
            object viewport = _root;
            while (Read(viewport, "VisualParent") is { } ancestor) viewport = ancestor;
            object viewportBounds = Read(viewport, "Bounds")!;
            RectData viewportClip = new(0, 0, Number(viewportBounds, "Width"), Number(viewportBounds, "Height"));
            List<object> nodes = new();
            Stack<(object Visual, int? Parent, bool Visible, RectData Clip)> pending = new();
            pending.Push((_root, null, true, viewportClip));
            Type controlType = _getNodeId.GetParameters()[0].ParameterType;
            while (pending.TryPop(out var entry))
            {
                object visual = entry.Visual;
                RectData? measured = Measure(visual, viewport);
                RectData clip = entry.Clip;
                if (Read(visual, "ClipToBounds") is true && measured is not null) clip = clip.Intersect(measured);
                if (Read(visual, "Clip") is { } geometry && Read(geometry, "Bounds") is { } geometryBounds
                    && Measure(visual, viewport, geometryBounds) is { } geometryClip) clip = clip.Intersect(geometryClip);
                bool visible = entry.Visible && Read(visual, "IsVisible") is not false && clip.Width > 0 && clip.Height > 0;
                int? parent = entry.Parent;
                if (controlType.IsInstanceOfType(visual)
                    && Guid.TryParse(_getNodeId.Invoke(null, new[] { visual }) as string, out Guid nodeId))
                {
                    if (measured is not null)
                    {
                        int instanceId = _instances.GetValue(visual, _ => new InstanceIdentity(++_nextInstanceId)).Value;
                        nodes.Add(new
                        {
                            NodeId = nodeId,
                            InstanceId = instanceId,
                            ParentInstanceId = parent,
                            TypeName = visual.GetType().FullName ?? visual.GetType().Name,
                            Name = Read(visual, "Name") as string,
                            measured.X, measured.Y, measured.Width, measured.Height,
                            IsVisible = visible,
                            Clip = clip,
                            Layout = CaptureLayout(visual, viewport)
                        });
                        parent = instanceId;
                    }
                }
                if (Read(visual, "VisualChildren") is IEnumerable children)
                    foreach (object child in children.Cast<object>().OrderBy(child => Read(child, "ZIndex") as int? ?? 0).Reverse())
                        pending.Push((child, parent, visible, clip));
            }
            string payload = JsonSerializer.Serialize(new
            {
                RenderId = _renderId,
                viewportClip.Width,
                viewportClip.Height,
                Nodes = nodes
            });
            if (payload == _lastPayload) return;
            _lastPayload = payload;
            _lastError = null;
            Console.WriteLine(MessagePrefix + payload);
        }
        catch (Exception exception)
        {
            string message = "Design tree unavailable: " + (exception.InnerException?.Message ?? exception.Message);
            if (message != _lastError) Console.Error.WriteLine(message);
            _lastError = message;
        }
    }

    private static object? Read(object instance, string property) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);

    private static double Number(object instance, string property) => (double)Read(instance, property)!;

    private object CaptureLayout(object control, object viewport)
    {
        Type controlType = _getNodeId.GetParameters()[0].ParameterType;
        object Value(string name) => controlType.GetProperty(name)!.GetValue(control)!;
        object bounds = Value("Bounds");
        object desired = Value("DesiredSize");
        object margin = Value("Margin");
        object? padding = Read(control, "Padding");
        if (padding?.GetType().FullName != "Avalonia.Thickness") padding = null;
        object origin = Value("RenderTransformOrigin");
        object originPoint = Read(origin, "Point")!;
        object matrix = _transformToVisual.Invoke(null, new[] { control, viewport })!;
        Type canvas = controlType.Assembly.GetType("Avalonia.Controls.Canvas", throwOnError: true)!;
        Type grid = controlType.Assembly.GetType("Avalonia.Controls.Grid", throwOnError: true)!;
        object Attached(Type owner, string name) => owner.GetMethod("Get" + name)!.Invoke(null, new[] { control })!;
        double? Finite(double value) => double.IsFinite(value) ? value : null;
        double[] Tracks(string collection, string size) => grid.IsInstanceOfType(control)
            ? ((IEnumerable)grid.GetProperty(collection)!.GetValue(control)!).Cast<object>().Select(track => Number(track, size)).ToArray()
            : Array.Empty<double>();
        string kind = "None";
        for (Type? type = control.GetType(); type is not null; type = type.BaseType)
        {
            if (type.Namespace != "Avalonia.Controls") continue;
            kind = type.Name switch
            {
                "Canvas" => "Canvas", "Grid" => "Grid", "StackPanel" => "Stack",
                "DockPanel" => "Dock", "WrapPanel" or "UniformGrid" or "Panel" => "Panel",
                "ItemsControl" => "Items", "Decorator" or "Viewbox" => "Child",
                "ContentControl" => "Content", _ => "None"
            };
            if (kind != "None") break;
        }
        return new
        {
            Kind = kind,
            X = Number(bounds, "X"), Y = Number(bounds, "Y"),
            Width = Number(bounds, "Width"), Height = Number(bounds, "Height"),
            DesiredWidth = Number(desired, "Width"), DesiredHeight = Number(desired, "Height"),
            MarginLeft = Number(margin, "Left"), MarginTop = Number(margin, "Top"),
            MarginRight = Number(margin, "Right"), MarginBottom = Number(margin, "Bottom"),
            PaddingLeft = padding is null ? 0 : Number(padding, "Left"),
            PaddingTop = padding is null ? 0 : Number(padding, "Top"),
            PaddingRight = padding is null ? 0 : Number(padding, "Right"),
            PaddingBottom = padding is null ? 0 : Number(padding, "Bottom"),
            MinWidth = (double)Value("MinWidth"), MinHeight = (double)Value("MinHeight"),
            MaxWidth = Finite((double)Value("MaxWidth")), MaxHeight = Finite((double)Value("MaxHeight")),
            CanvasLeft = Finite((double)Attached(canvas, "Left")), CanvasTop = Finite((double)Attached(canvas, "Top")),
            CanvasRight = Finite((double)Attached(canvas, "Right")), CanvasBottom = Finite((double)Attached(canvas, "Bottom")),
            GridRow = (int)Attached(grid, "Row"), GridColumn = (int)Attached(grid, "Column"),
            GridRowSpan = (int)Attached(grid, "RowSpan"), GridColumnSpan = (int)Attached(grid, "ColumnSpan"),
            RowHeights = Tracks("RowDefinitions", "ActualHeight"), ColumnWidths = Tracks("ColumnDefinitions", "ActualWidth"),
            RowSpacing = grid.IsInstanceOfType(control) ? (double)grid.GetProperty("RowSpacing")!.GetValue(control)! : 0,
            ColumnSpacing = grid.IsInstanceOfType(control) ? (double)grid.GetProperty("ColumnSpacing")!.GetValue(control)! : 0,
            IsHorizontal = Read(control, "Orientation")?.ToString() == "Horizontal",
            Spacing = Read(control, "Spacing") is double spacing ? spacing : 0,
            HorizontalAlignment = Value("HorizontalAlignment").ToString(),
            VerticalAlignment = Value("VerticalAlignment").ToString(),
            OriginX = Number(originPoint, "X"), OriginY = Number(originPoint, "Y"),
            OriginIsRelative = Read(origin, "Unit")?.ToString() == "Relative",
            M11 = Number(matrix, "M11"), M12 = Number(matrix, "M12"), M21 = Number(matrix, "M21"),
            M22 = Number(matrix, "M22"), M31 = Number(matrix, "M31"), M32 = Number(matrix, "M32")
        };
    }

    private RectData? Measure(object visual, object viewport, object? localBounds = null)
    {
        object? transform = _transformToVisual.Invoke(null, new[] { visual, viewport });
        object? bounds = Read(visual, "Bounds");
        if (transform is null || bounds is null) return null;
        localBounds ??= Activator.CreateInstance(bounds.GetType(), 0d, 0d, Number(bounds, "Width"), Number(bounds, "Height"))!;
        object transformed = bounds.GetType().GetMethod("TransformToAABB", new[] { transform.GetType() })!
            .Invoke(localBounds, new[] { transform })!;
        return new RectData(Number(transformed, "X"), Number(transformed, "Y"),
            Number(transformed, "Width"), Number(transformed, "Height"));
    }

    private sealed record RectData(double X, double Y, double Width, double Height)
    {
        public RectData Intersect(RectData other)
        {
            double left = Math.Max(X, other.X);
            double top = Math.Max(Y, other.Y);
            return new(left, top, Math.Max(0, Math.Min(X + Width, other.X + other.Width) - left),
                Math.Max(0, Math.Min(Y + Height, other.Y + other.Height) - top));
        }
    }

    private sealed record InstanceIdentity(int Value);
}
