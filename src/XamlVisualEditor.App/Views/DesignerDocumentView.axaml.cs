using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Designer.Adorners;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Code-behind for the designer document view (split design + code).
/// </summary>
public sealed partial class DesignerDocumentView : UserControl
{
    private bool _isAttached;
    private DesignerDocumentViewModel? _rulerDocument;

    public DesignerDocumentView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (_isAttached) AttachRulerDocument(DataContext as DesignerDocumentViewModel); };
        LayoutUpdated += (_, _) => UpdatePreviewerRulers();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        AttachRulerDocument(DataContext as DesignerDocumentViewModel);
        if (this.FindControl<ScrollViewer>("PreviewerViewport") is { } viewport)
        {
            // Also sees moves the surface handles, so the strip follows drags and positions outside the document.
            viewport.AddHandler(PointerMovedEvent, OnPreviewerViewportMoved, RoutingStrategies.Bubble, handledEventsToo: true);
            viewport.PointerExited += OnPreviewerViewportExited;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        AttachRulerDocument(null);
        if (this.FindControl<ScrollViewer>("PreviewerViewport") is { } viewport)
        {
            viewport.RemoveHandler(PointerMovedEvent, OnPreviewerViewportMoved);
            viewport.PointerExited -= OnPreviewerViewportExited;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPreviewerViewportMoved(object? sender, PointerEventArgs args)
    {
        if (this.FindControl<PreviewerSurfaceControl>("ExternalPreviewSurface") is { } surface)
            surface.ReportPointer(args.GetPosition(surface));
    }

    private void OnPreviewerViewportExited(object? sender, PointerEventArgs args) =>
        this.FindControl<PreviewerSurfaceControl>("ExternalPreviewSurface")?.ReportPointer(null);

    private void AttachRulerDocument(DesignerDocumentViewModel? document)
    {
        if (_rulerDocument is not null) _rulerDocument.DesignSurface.Selection.SelectionChanged -= OnPreviewSelectionChanged;
        _rulerDocument = document;
        if (document is not null) document.DesignSurface.Selection.SelectionChanged += OnPreviewSelectionChanged;
        UpdatePreviewerRulers();
    }

    private void OnPreviewSelectionChanged(IReadOnlyList<IDesignItem> _) => UpdatePreviewerRulers();

    private void UpdatePreviewerRulers()
    {
        // Runs after every layout pass of the window; a hidden view is updated by the pass that shows it.
        if (_rulerDocument?.UseExternalPreviewer != true || !IsEffectivelyVisible) return;
        PreviewerSurfaceControl? surface = this.FindControl<PreviewerSurfaceControl>("ExternalPreviewSurface");
        RulerControl? horizontal = this.FindControl<RulerControl>("ExternalHorizontalRuler");
        RulerControl? vertical = this.FindControl<RulerControl>("ExternalVerticalRuler");
        if (surface?.TranslatePoint(default, this) is not Point origin || horizontal is null || vertical is null) return;
        if (horizontal.TranslatePoint(default, this) is Point horizontalOrigin) horizontal.Offset = horizontalOrigin.X - origin.X;
        if (vertical.TranslatePoint(default, this) is Point verticalOrigin) vertical.Offset = verticalOrigin.Y - origin.Y;
        Rect? selection = null;
        foreach (IDesignItem item in _rulerDocument.DesignSurface.Selection.SelectedItems)
            if (item is DesignItem { HostBounds: { Width: > 0, Height: > 0 } bounds })
                selection = selection is { } current ? current.Union(bounds) : bounds;
        horizontal.SelectionStart = selection?.Left;
        horizontal.SelectionEnd = selection?.Right;
        vertical.SelectionStart = selection?.Top;
        vertical.SelectionEnd = selection?.Bottom;
    }

    private void OnPreviewerFit(object? sender, RoutedEventArgs args) => FitPreviewer(false);
    private void OnPreviewerSelection(object? sender, RoutedEventArgs args) => FitPreviewer(true);

    private void OnPreviewerViewportPressed(object? sender, PointerPressedEventArgs args)
    {
        // The surface handles presses on the document; a press on the empty viewport around it clears the selection.
        PreviewerSurfaceControl? surface = this.FindControl<PreviewerSurfaceControl>("ExternalPreviewSurface");
        if (surface is null || args.Source is not Visual source || surface.IsVisualAncestorOf(source)
            || source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null
            || args.KeyModifiers != KeyModifiers.None || !args.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) return;
        surface.ClearSelectionFromViewport();
    }

    private void FitPreviewer(bool selection)
    {
        ScrollViewer? viewport = this.FindControl<ScrollViewer>("PreviewerViewport");
        PreviewerSurfaceControl? surface = this.FindControl<PreviewerSurfaceControl>("ExternalPreviewSurface");
        if (viewport is null || surface is null) return;
        if (surface.FitToViewport(viewport.Viewport, selection) is not { } target) return;
        surface.UpdateLayout();
        double scale = surface.Document?.DesignSurface.Zoom ?? 1;
        double gap = surface.Margin.Left;
        viewport.Offset = selection
            ? new Vector(Math.Max(0, target.Center.X * scale + gap - viewport.Viewport.Width / 2),
                Math.Max(0, target.Center.Y * scale + gap - viewport.Viewport.Height / 2)) : default;
    }
}
