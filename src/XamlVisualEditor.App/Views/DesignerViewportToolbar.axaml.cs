using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace XamlVisualEditor.App.Views;

public partial class DesignerViewportToolbar : UserControl
{
    public static readonly StyledProperty<bool> IsExternalPreviewerProperty =
        AvaloniaProperty.Register<DesignerViewportToolbar, bool>(nameof(IsExternalPreviewer));

    public bool IsExternalPreviewer
    {
        get => GetValue(IsExternalPreviewerProperty);
        set => SetValue(IsExternalPreviewerProperty, value);
    }

    public event EventHandler<RoutedEventArgs>? FitRequested;
    public event EventHandler<RoutedEventArgs>? SelectionRequested;

    public DesignerViewportToolbar() => AvaloniaXamlLoader.Load(this);

    private void OnFitClick(object? sender, RoutedEventArgs args) { FitRequested?.Invoke(this, args); args.Handled = true; }
    private void OnSelectionClick(object? sender, RoutedEventArgs args) { SelectionRequested?.Invoke(this, args); args.Handled = true; }
}
