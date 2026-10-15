using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace XamlVisualEditor.Designer.Rendering;

/// <summary>Presents window content visually while retaining its actual logical window ancestor.</summary>
public sealed class DesignWindowContentHost : Control, IDisposable
{
    private readonly Window _window;
    private readonly CompositeDisposable _subscriptions = new();
    private Control? _content;
    private bool _ownsContentParent;
    private bool _disposed;

    public DesignWindowContentHost(Window window)
    {
        _window = window;
        DesignNodeIdentity.SetNodeId(this, DesignNodeIdentity.GetNodeId(window));
        LogicalChildren.Add(window);
        _subscriptions.Add(Bind(WidthProperty, window.GetObservable(WidthProperty)));
        _subscriptions.Add(Bind(HeightProperty, window.GetObservable(HeightProperty)));
        _subscriptions.Add(Bind(MinWidthProperty, window.GetObservable(MinWidthProperty)));
        _subscriptions.Add(Bind(MinHeightProperty, window.GetObservable(MinHeightProperty)));
        _subscriptions.Add(Bind(MaxWidthProperty, window.GetObservable(MaxWidthProperty)));
        _subscriptions.Add(Bind(MaxHeightProperty, window.GetObservable(MaxHeightProperty)));
        _subscriptions.Add(window.GetObservable(Window.ContentProperty).Subscribe(SetContent));
        _subscriptions.Add(window.GetObservable(Window.PaddingProperty).Subscribe(_ => InvalidateMeasure()));
        _subscriptions.Add(window.GetObservable(Window.BackgroundProperty).Subscribe(_ => InvalidateVisual()));
    }

    private void SetContent(object? value)
    {
        if (_content is not null)
        {
            VisualChildren.Remove(_content);
            if (_ownsContentParent) ((ISetLogicalParent)_content).SetParent(null);
        }
        _ownsContentParent = false;
        _content = value as Control;
        if (value is not null && _content is null)
            _content = new ContentPresenter { Content = value, ContentTemplate = _window.ContentTemplate };
        if (_content is not null)
        {
            if (_content.GetVisualParent() is not null)
                throw new InvalidOperationException("The window content already belongs to another visual presenter.");
            if (_content.Parent is null)
            {
                ((ISetLogicalParent)_content).SetParent(_window);
                _ownsContentParent = true;
            }
            VisualChildren.Add(_content);
        }
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Thickness padding = _window.Padding;
        Size inner = new(Math.Max(0, availableSize.Width - padding.Left - padding.Right),
            Math.Max(0, availableSize.Height - padding.Top - padding.Bottom));
        _content?.Measure(inner);
        Size desired = _content?.DesiredSize ?? default;
        return new Size(desired.Width + padding.Left + padding.Right, desired.Height + padding.Top + padding.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Thickness padding = _window.Padding;
        _content?.Arrange(new Rect(padding.Left, padding.Top,
            Math.Max(0, finalSize.Width - padding.Left - padding.Right),
            Math.Max(0, finalSize.Height - padding.Top - padding.Bottom)));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_window.Background is { } background) context.DrawRectangle(background, null, new Rect(Bounds.Size));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
        SetContent(null);
        LogicalChildren.Remove(_window);
        _window.Content = null;
        _window.Close();
    }
}
