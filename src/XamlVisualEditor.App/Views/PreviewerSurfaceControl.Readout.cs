using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using XamlVisualEditor.Designer.Adorners;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

public sealed partial class PreviewerSurfaceControl
{
    private Point? _measureAnchor;
    private (Rect? Bounds, int Count)? _reportedSelection;
    private bool _selectionReportPending;

    private DesignerCoordinateReadout? Readout =>
        _document is { IsDisposed: false, UseExternalPreviewer: true } document ? document.DesignSurface.Readout : null;

    /// <summary>
    /// Reports a view position of the pointer to the coordinate strip, or null when the pointer left the viewport.
    /// The position may lie outside the rendered document.
    /// </summary>
    public void ReportPointer(Point? position)
    {
        if (Readout is not { } readout)
        {
            return;
        }

        if (position is not { } point || ToHostPoint(point) is not { } host)
        {
            // A captured drag or measurement keeps reporting positions outside the viewport.
            if (_measureAnchor is not null || _pointerMove is not null) return;
            readout.SetPointer(null);
            readout.SetHover(null, null);
            return;
        }

        readout.SetPointer(host);
        DesignerHostNode? node = _pointerMove is null ? HitNode(host) : null;
        readout.SetHover(node?.TypeName, node?.Name);
    }

    private Point? ToHostPoint(Point position)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return null;
        }

        // Same mapping as the input forwarded to the previewer.
        return new Point(position.X * (_naturalWidth ?? Bounds.Width / PreviewZoom) / Bounds.Width,
            position.Y * (_naturalHeight ?? Bounds.Height / PreviewZoom) / Bounds.Height);
    }

    private DesignerHostNode? HitNode(Point host) => _designTree?.Nodes.Reverse().FirstOrDefault(node => node.IsVisible
        && _hostItems.ContainsKey(node.InstanceId) && new Rect(node.X, node.Y, node.Width, node.Height).Contains(host)
        && (node.Clip is not { } clip || new Rect(clip.X, clip.Y, clip.Width, clip.Height).Contains(host)));

    private bool TryStartMeasure(PointerPressedEventArgs args)
    {
        if (Readout is not { } readout || _document?.DesignSurface.IsMeasuring != true
            || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed || ToHostPoint(args.GetPosition(this)) is not { } anchor)
        {
            return false;
        }

        Focus();
        _measureAnchor = anchor;
        readout.SetMeasure(anchor, anchor);
        args.Pointer.Capture(this);
        args.Handled = true;
        InvalidateVisual();
        return true;
    }

    private bool UpdateMeasure(PointerEventArgs args)
    {
        if (_measureAnchor is not { } anchor || Readout is not { } readout)
        {
            return false;
        }

        if (ToHostPoint(args.GetPosition(this)) is { } end) readout.SetMeasure(anchor, end);
        args.Handled = true;
        InvalidateVisual();
        return true;
    }

    private bool EndMeasure(PointerReleasedEventArgs args)
    {
        if (_measureAnchor is null)
        {
            return false;
        }

        // The measured distance stays visible until the next measurement or until measuring ends.
        _measureAnchor = null;
        args.Pointer.Capture(null);
        args.Handled = true;
        return true;
    }

    private void DrawMeasure(DrawingContext context)
    {
        if (Readout is { MeasureStart: { } start, MeasureEnd: { } end })
            DesignMeasureLayer.Draw(context, start, end, PreviewZoom);
    }

    /// <summary>Hands the selection bounds of the last render to the coordinate strip after the render pass.</summary>
    private void ReportSelection(Rect? bounds, int count)
    {
        if (_reportedSelection is { } reported && reported.Bounds == bounds && reported.Count == count)
        {
            return;
        }

        _reportedSelection = (bounds, count);
        if (_selectionReportPending)
        {
            return;
        }

        _selectionReportPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _selectionReportPending = false;
            if (_reportedSelection is { } selection) Readout?.SetSelection(selection.Bounds, selection.Count);
        }, DispatcherPriority.Background);
    }
}
