using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Xaml.Interactivity;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.App.Views;

/// <summary>Keeps the editor colors and the editor's light/dark choice out of the local form preview.</summary>
public sealed class EditorThemeIsolationBehavior : Behavior<ThemeVariantScope>
{
    public static readonly StyledProperty<IEditorThemeService?> ServiceProperty =
        AvaloniaProperty.Register<EditorThemeIsolationBehavior, IEditorThemeService?>(nameof(Service));
    private ResourceDictionary? _resources;
    private bool _settingVariant;
    private bool _usingDefaultVariant;

    public IEditorThemeService? Service { get => GetValue(ServiceProperty); set => SetValue(ServiceProperty, value); }

    protected override void OnAttached()
    {
        base.OnAttached();
        ConfigureScope();
    }

    protected override void OnDetaching()
    {
        ClearScope();
        base.OnDetaching();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ServiceProperty && AssociatedObject is not null) ConfigureScope();
    }

    private void ConfigureScope()
    {
        ClearScope();
        if (AssociatedObject is not { } scope || Service is not EditorThemeService service) return;
        _resources = service.CreatePreviewResources();
        scope.Resources.MergedDictionaries.Insert(0, _resources);
        scope.PropertyChanged += OnScopePropertyChanged;
        ApplyDefaultVariant();
    }

    private void OnScopePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == ThemeVariantScope.RequestedThemeVariantProperty && !_settingVariant) ApplyDefaultVariant();
    }

    private void ApplyDefaultVariant()
    {
        if (AssociatedObject is not { } scope || Service is not EditorThemeService service) return;
        _usingDefaultVariant = scope.RequestedThemeVariant is null || scope.RequestedThemeVariant == ThemeVariant.Default;
        if (!_usingDefaultVariant) return;
        _settingVariant = true;
        try { scope.SetCurrentValue(ThemeVariantScope.RequestedThemeVariantProperty, service.PreviewDefaultVariant); }
        finally { _settingVariant = false; }
    }

    private void ClearScope()
    {
        if (AssociatedObject is not { } scope) return;
        scope.PropertyChanged -= OnScopePropertyChanged;
        if (_resources is not null) scope.Resources.MergedDictionaries.Remove(_resources);
        _resources = null;
        if (_usingDefaultVariant) scope.SetCurrentValue(ThemeVariantScope.RequestedThemeVariantProperty, ThemeVariant.Default);
        _usingDefaultVariant = false;
    }
}
