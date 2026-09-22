using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.App.Views;

/// <summary>Ändert die Fenstergröße relativ zum angeklickten Punkt, ohne einen Sprung zur Fensterecke.</summary>
public sealed class WindowResizeGrip : Control
{
    public static readonly StyledProperty<IBrush?> GripBrushProperty =
        AvaloniaProperty.Register<WindowResizeGrip, IBrush?>(nameof(GripBrush), Brushes.Gray);
    private Window? _window;
    private IPointer? _pointer;
    private PixelPoint _startPosition;
    private Size _startSize;
    private double _startScaling;

    static WindowResizeGrip() => AffectsRender<WindowResizeGrip>(GripBrushProperty);

    public WindowResizeGrip()
    {
        Width = MaxWidth = 16;
        Height = MaxHeight = 16;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Cursor = new Cursor(StandardCursorType.BottomRightCorner);
        ToolTip.SetTip(this, UiStrings.ResizeWindow);
        AutomationProperties.SetName(this, UiStrings.ResizeWindow);
    }

    public IBrush? GripBrush { get => GetValue(GripBrushProperty); set => SetValue(GripBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        Pen pen = new(GripBrush, 1);
        for (int offset = 4; offset <= 12; offset += 4)
            context.DrawLine(pen, new Point(offset, 13), new Point(13, offset));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.PropertyChanged += OnWindowPropertyChanged;
        UpdateAvailability();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EndDrag();
        if (_window is not null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.CanResizeProperty || e.Property == Window.WindowStateProperty) UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        IsVisible = _window is { CanResize: true, WindowState: WindowState.Normal };
        if (!IsVisible) EndDrag();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || _window is not { CanResize: true, WindowState: WindowState.Normal }
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _startPosition = _window.PointToScreen(e.GetPosition(_window));
        _startSize = _window.ClientSize;
        _startScaling = _window.RenderScaling;
        _pointer = e.Pointer;
        _pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_window is null || _pointer != e.Pointer) return;
        PixelPoint position = _window.PointToScreen(e.GetPosition(_window));
        _window.Width = Math.Clamp(_startSize.Width + (position.X - _startPosition.X) / _startScaling, _window.MinWidth, _window.MaxWidth);
        _window.Height = Math.Clamp(_startSize.Height + (position.Y - _startPosition.Y) / _startScaling, _window.MinHeight, _window.MaxHeight);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pointer != e.Pointer || e.InitialPressMouseButton != MouseButton.Left) return;
        EndDrag();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _pointer = null;
        base.OnPointerCaptureLost(e);
    }

    private void EndDrag()
    {
        IPointer? pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
    }
}
