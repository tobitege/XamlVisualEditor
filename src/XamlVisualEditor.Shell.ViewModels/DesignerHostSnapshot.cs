namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>A measured visual tree belonging to one preview-only XAML revision.</summary>
public sealed record DesignerHostSnapshot(
    Guid RenderId, double Width, double Height, IReadOnlyList<DesignerHostNode> Nodes);

/// <summary>Source identity and geometry of one live instance, including repeated template instances.</summary>
public sealed record DesignerHostNode(
    Guid NodeId, int InstanceId, int? ParentInstanceId, string TypeName, string? Name,
    double X, double Y, double Width, double Height, bool IsVisible, DesignerHostRect? Clip = null,
    DesignerHostLayout? Layout = null);

public sealed record DesignerHostRect(double X, double Y, double Width, double Height);

/// <summary>Effective layout inputs measured in the host, independent of the control's source declarations.</summary>
public sealed record DesignerHostLayout
{
    public string Kind { get; init; } = "None";
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double DesiredWidth { get; init; }
    public double DesiredHeight { get; init; }
    public double MarginLeft { get; init; }
    public double MarginTop { get; init; }
    public double MarginRight { get; init; }
    public double MarginBottom { get; init; }
    public double PaddingLeft { get; init; }
    public double PaddingTop { get; init; }
    public double PaddingRight { get; init; }
    public double PaddingBottom { get; init; }
    public double MinWidth { get; init; }
    public double MinHeight { get; init; }
    public double? MaxWidth { get; init; }
    public double? MaxHeight { get; init; }
    public double? CanvasLeft { get; init; }
    public double? CanvasTop { get; init; }
    public double? CanvasRight { get; init; }
    public double? CanvasBottom { get; init; }
    public int GridRow { get; init; }
    public int GridColumn { get; init; }
    public int GridRowSpan { get; init; } = 1;
    public int GridColumnSpan { get; init; } = 1;
    public IReadOnlyList<double> RowHeights { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> ColumnWidths { get; init; } = Array.Empty<double>();
    public double RowSpacing { get; init; }
    public double ColumnSpacing { get; init; }
    public bool IsHorizontal { get; init; }
    public double Spacing { get; init; }
    public string HorizontalAlignment { get; init; } = "Stretch";
    public string VerticalAlignment { get; init; } = "Stretch";
    public double OriginX { get; init; }
    public double OriginY { get; init; }
    public bool OriginIsRelative { get; init; }
    public double M11 { get; init; } = 1;
    public double M12 { get; init; }
    public double M21 { get; init; }
    public double M22 { get; init; } = 1;
    public double M31 { get; init; }
    public double M32 { get; init; }
}
