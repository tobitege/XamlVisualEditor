using System;
using System.Linq;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using Dock.Avalonia.Controls;
using Dock.Controls.ProportionalStackPanel;
using Dock.Model.Controls;
using Dock.Model.Core;
using ReactiveUI;
using XamlVisualEditor.Shell;
using ToolDock = Dock.Model.ReactiveUI.Controls.ToolDock;

namespace XamlVisualEditor.App.Views;

/// <summary>Keeps the widths of the outer docks when the window size changes.</summary>
public sealed class FixedSideDockWidthsBehavior : Behavior<DockControl>
{
    private const double MINIMUM_EDITOR_WIDTH = 240;
    private IDisposable? _layoutSubscription;
    private IDisposable? _proportionSubscription;
    private IDockable? _left;
    private IDockable? _right;
    private IDockable? _center;
    private double _width;
    private double _leftWidth;
    private double _rightWidth;
    private bool _updating;

    protected override void OnAttached()
    {
        base.OnAttached();
        DockControl control = AssociatedObject!;
        control.SizeChanged += OnSizeChanged;
        _layoutSubscription = control.GetObservable(DockControl.LayoutProperty)
            .Subscribe(layout => BindLayout(layout as IRootDock));
    }

    protected override void OnDetaching()
    {
        AssociatedObject!.SizeChanged -= OnSizeChanged;
        _layoutSubscription?.Dispose();
        _proportionSubscription?.Dispose();
        _layoutSubscription = null;
        _proportionSubscription = null;
        _left = _right = _center = null;
        base.OnDetaching();
    }

    private void BindLayout(IRootDock? root)
    {
        _proportionSubscription?.Dispose();
        _left = root is null ? null : XamlEditorDockFactory.FindDockable<IDockable>(root, "LeftToolDock");
        _right = root is null ? null : XamlEditorDockFactory.FindDockable<IDockable>(root, "RightToolDock");
        _center = root is null ? null : XamlEditorDockFactory.FindDockable<IDockable>(root, "CenterAndBottom");
        _width = AssociatedObject?.Bounds.Width ?? 0;
        CaptureWidths();
        if (_left is ToolDock left && _right is ToolDock right)
        {
            _proportionSubscription = Observable.Merge(
                    left.WhenAnyValue(dock => dock.Proportion).Skip(1),
                    right.WhenAnyValue(dock => dock.Proportion).Skip(1))
                .Subscribe(_ => { if (!_updating) CaptureWidths(); });
        }
    }

    private void CaptureWidths()
    {
        if (_width <= 0 || _left is null || _right is null) return;
        _leftWidth = double.IsFinite(_left.Proportion) ? Math.Max(0, _left.Proportion) * _width : 0;
        _rightWidth = double.IsFinite(_right.Proportion) ? Math.Max(0, _right.Proportion) * _width : 0;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        double width = args.NewSize.Width;
        if (width <= 0 || _left is null || _right is null || _center is null) return;
        if (_width <= 0)
        {
            _width = width;
            CaptureWidths();
            return;
        }
        if (Math.Abs(width - _width) < 0.5) return;

        double sideWidth = _leftWidth + _rightWidth;
        double scale = sideWidth > 0
            ? Math.Min(1, Math.Max(0, width - MINIMUM_EDITOR_WIDTH) / sideWidth) : 1;
        double left = _leftWidth * scale / width;
        double right = _rightWidth * scale / width;
        _width = width;
        _updating = true;
        try
        {
            _left.Proportion = left;
            _right.Proportion = right;
            _center.Proportion = 1 - left - right;
            ApplyVisualProportions(left, 1 - left - right, right);
        }
        finally { _updating = false; }
    }

    private void ApplyVisualProportions(double left, double center, double right)
    {
        ProportionalDockControl? main = AssociatedObject?.GetVisualDescendants()
            .OfType<ProportionalDockControl>()
            .FirstOrDefault(control => control.DataContext is IDockable { Id: "MainLayout" });
        ProportionalStackPanel? panel = main?.GetVisualDescendants()
            .OfType<ProportionalStackPanel>().FirstOrDefault();
        if (panel is null) return;

        foreach (Control child in panel.Children)
        {
            if (child.DataContext is not IDockable dockable) continue;
            double proportion = dockable.Id switch
            {
                "LeftToolDock" => left,
                "CenterAndBottom" => center,
                "RightToolDock" => right,
                _ => double.NaN
            };
            if (double.IsFinite(proportion))
                child.SetCurrentValue(ProportionalStackPanel.ProportionProperty, proportion);
        }
    }
}
