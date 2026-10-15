using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using System.Reactive.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Avalonia.Themes.Fluent;
using ReactiveUI.Primitives.Concurrency;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class EditorThemeTests
{

    [AvaloniaFact]
    public async Task FailedThemePreviewKeepsTheLastValidPreviewAndCancelRestoresTheOriginal()
    {
        using ThemeHost host = new();
        string providerPath = CopyProvider(typeof(SampleEditorThemeProvider).Assembly.Location);
        using IDisposable registration = host.Registry.RegisterEditorThemeProviders("example.generic", new[]
        {
            new EditorThemeProviderRegistration("example.generic/palette", "Generic", providerPath, typeof(SampleEditorThemeProvider).FullName!)
        });
        using EditorThemeService themes = new(host.Registry, host.Manager, host.Options);
        EditorThemeSelection original = themes.Selection;
        using OptionsDialogViewModel options = new(host.Options, themes);
        options.SelectedEditorThemeProvider = options.EditorThemeProviders.Single(item => item.Id == "example.generic/palette");
        EditorThemeSelection preview = themes.Selection;
        options.SelectedEditorTheme = options.EditorThemes.Single(item => item.Id == "broken");
        Assert.NotNull(options.EditorThemeError);
        Assert.Equal(preview, themes.Selection);
        await options.CancelCommand.Execute().FirstAsync();
        Assert.Equal(original, themes.Selection);
        Assert.Equal(original, host.Options.Current.EditorTheme);
    }

    [AvaloniaFact]
    public void ClosingPreviewAfterProviderRemovalRetainsOriginalSelectionAndFallsBack()
    {
        using ThemeHost host = new();
        string providerPath = CopyProvider(typeof(SampleEditorThemeProvider).Assembly.Location);
        using IDisposable registration = host.Registry.RegisterEditorThemeProviders("example.generic", new[]
        {
            new EditorThemeProviderRegistration("example.generic/palette", "Generic", providerPath, typeof(SampleEditorThemeProvider).FullName!)
        });
        EditorThemeSelection original = new("example.generic/palette", "violet");
        host.Options.Save(host.Options.Current with { EditorTheme = original });
        using EditorThemeService themes = new(host.Registry, host.Manager, host.Options);
        OptionsDialogViewModel options = new(host.Options, themes);
        options.SelectedEditorThemeProvider = options.EditorThemeProviders.Single(item => item.Id == "builtin");
        registration.Dispose();
        Dispatcher.UIThread.RunJobs();
        options.Dispose();
        Assert.Equal(original, themes.Selection);
        Assert.Equal(original, host.Options.Current.EditorTheme);
        Assert.NotNull(themes.Error);
        Assert.Equal(ThemeVariant.Dark, host.Application.ActualThemeVariant);
    }

    [AvaloniaTheory]
    [InlineData("light", true)]
    [InlineData("dark", false)]
    public void CanvasAndBusyBackdropsFollowTheBuiltInTheme(string theme, bool expectLight)
    {
        using ThemeHost host = new();
        using EditorThemeService themes = new(host.Registry, host.Manager, host.Options);
        themes.Apply(new("builtin", theme));

        foreach (string key in new[] { "EditorCanvasBackdropBrush", "EditorBusyBackdropBrush" })
        {
            Assert.True(host.Application.TryGetResource(key, host.Application.ActualThemeVariant, out object? value));
            Color color = Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
            Assert.Equal(expectLight, (color.R + color.G + color.B) / 3 > 128);
        }
    }

    [AvaloniaFact]
    public void ProviderPaletteOverridesTheBuiltInBackdrops()
    {
        using ThemeHost host = new();
        EditorThemePalette palette = new SampleEditorThemeProvider().GetPalette("violet");
        host.Application.RequestedThemeVariant = ThemeVariant.Light;
        host.Application.Resources = EditorThemeResources.Create(palette);
        Color background = Color.Parse(palette.Colors[EditorThemeColor.Background]);

        Assert.True(host.Application.TryGetResource("EditorCanvasBackdropBrush", ThemeVariant.Light, out object? canvas));
        Assert.True(host.Application.TryGetResource("EditorBusyBackdropBrush", ThemeVariant.Light, out object? busy));
        Assert.Equal(background, Assert.IsAssignableFrom<ISolidColorBrush>(canvas).Color);
        Assert.Equal(Color.FromArgb(208, background.R, background.G, background.B), Assert.IsAssignableFrom<ISolidColorBrush>(busy).Color);
    }

    [AvaloniaFact]
    public async Task GenericEditorThemePersistsWhileLocalPreviewKeepsItsOwnResourcesAndVariant()
    {
        using ThemeHost host = new();
        string providerPath = CopyProvider(typeof(SampleEditorThemeProvider).Assembly.Location);
        using IDisposable registration = host.Registry.RegisterEditorThemeProviders("example.generic", new[]
        {
            new EditorThemeProviderRegistration("example.generic/palette", "Generic", providerPath, typeof(SampleEditorThemeProvider).FullName!)
        });
        using EditorThemeService themes = new(host.Registry, host.Manager, host.Options);
        EditorThemeSelection choice = new("example.generic/palette", "violet");
        Assert.Null(themes.Error);
        Assert.Equal(2, themes.Catalogs.Count);

        DesignSurfaceViewModel surface = new() { EditorThemeService = themes, CanvasWidth = 200, CanvasHeight = 100 };
        DesignSurfaceView preview = new() { DataContext = surface };
        Window window = new() { Content = preview, Width = 500, Height = 350 };
        window.Show();
        try
        {
            Flush(window);
            ThemeVariantScope scope = Assert.Single(preview.GetVisualDescendants().OfType<ThemeVariantScope>());
            Assert.Equal(ThemeVariant.Dark, scope.RequestedThemeVariant);
            Assert.Equal(ThemeVariant.Dark, scope.ActualThemeVariant);
            Color previewBackground = BrushColor(scope, "SystemControlBackgroundAltHighBrush");
            Panel canvas = preview.FindControl<Panel>("DesignCanvas")!;
            Color renderedPreview = Assert.IsAssignableFrom<ISolidColorBrush>(canvas.Background).Color;
            using OptionsDialogViewModel options = new(host.Options, themes);
            options.SelectedEditorThemeProvider = options.EditorThemeProviders.Single(provider => provider.Id == choice.ProviderId);
            options.SelectedEditorTheme = options.EditorThemes.Single(theme => theme.Id == choice.ThemeId);
            await options.SaveCommand.Execute().FirstAsync();
            Assert.Null(options.Error);
            Assert.Equal(choice, host.Options.Current.EditorTheme);
            Assert.Equal(choice, new EditorOptionsStore(host.OptionsPath).Current.EditorTheme);
            Assert.Equal(ThemeVariant.Light, host.Application.ActualThemeVariant);
            Assert.Equal(Color.Parse("#6840B8"), BrushColor(host.Application, "EditorStatusBarBackgroundBrush"));
            Assert.Equal(Color.Parse("#6840B8"), BrushColor(host.Application, "DockTabActiveBackgroundBrush"));
            Assert.Equal(Color.Parse("#6840B8"), BrushColor(host.Application, "DataGridCellSelectedBackgroundBrush"));
            Flush(window);
            Assert.Equal(Color.Parse("#F2EDF9"), Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
            Assert.Equal(ThemeVariant.Dark, scope.RequestedThemeVariant);
            Assert.Equal(ThemeVariant.Dark, scope.ActualThemeVariant);
            Assert.Equal(previewBackground, BrushColor(scope, "SystemControlBackgroundAltHighBrush"));
            Assert.Equal(renderedPreview, Assert.IsAssignableFrom<ISolidColorBrush>(canvas.Background).Color);
            Assert.Equal(ThemeVariant.Default, surface.PreviewThemeVariant);
            surface.PreviewThemeVariant = ThemeVariant.Light;
            Flush(window);
            Assert.Equal(ThemeVariant.Light, scope.RequestedThemeVariant);

            IResourceDictionary applied = host.Application.Resources;
            Assert.Throws<InvalidOperationException>(() => themes.Apply(choice with { ThemeId = "broken" }));
            Assert.Same(applied, host.Application.Resources);
            Assert.Equal(choice, themes.Selection);
            using FileStream unlocked = File.Open(providerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(typeof(SampleEditorThemeProvider))]
    [InlineData(typeof(ThrowingDisposeEditorThemeProvider))]
    public async Task ProviderDisableReenableAndReplacementRestoreResourcesAndSavedSelection(Type initialProvider)
    {
        using ThemeHost host = new();
        string providerPath = CopyProvider(typeof(SampleEditorThemeProvider).Assembly.Location);
        string providerId = "example.generic/palette";
        IDisposable registration = host.Registry.RegisterEditorThemeProviders("example.generic", new[]
        {
            new EditorThemeProviderRegistration(providerId, "Generic", providerPath, initialProvider.FullName!)
        });
        host.Options.Save(host.Options.Current with { EditorTheme = new(providerId, "violet") });
        using EditorThemeService themes = new(host.Registry, host.Manager, host.Options);
        Assert.Null(themes.Error);
        Assert.Equal(Color.Parse("#6840B8"), BrushColor(host.Application, "EditorStatusBarBackgroundBrush"));
        await host.Manager.ChangeAsync("example.generic", registration.Dispose);
        Assert.NotNull(themes.Error);
        Assert.Equal(host.Options.Current.EditorTheme, themes.Selection);
        Assert.DoesNotContain(themes.Catalogs, catalog => catalog.Id == providerId);
        await host.Manager.ChangeAsync("example.generic", () => registration = host.Registry.RegisterEditorThemeProviders("example.generic", new[]
        {
            new EditorThemeProviderRegistration(providerId, "Updated", providerPath, typeof(UpdatedEditorThemeProvider).FullName!)
        }));
        Assert.Null(themes.Error);
        Assert.Equal(Color.Parse("#118844"), BrushColor(host.Application, "EditorStatusBarBackgroundBrush"));
        registration.Dispose();
    }

    [AvaloniaFact]
    public async Task InvalidatedActiveCatalogFallsBackWithoutLosingTheSavedTheme()
    {
        using ThemeHost host = new();
        string providerPath = CopyProvider(typeof(SampleEditorThemeProvider).Assembly.Location);
        using IDisposable registration = host.Registry.RegisterEditorThemeProviders("example.generic", new[]
        {
            new EditorThemeProviderRegistration("example.generic/palette", "Generic", providerPath, typeof(InvalidCatalogEditorThemeProvider).FullName!)
        });
        host.Options.Save(host.Options.Current with { EditorTheme = new("example.generic/palette", "violet") });
        using EditorThemeService themes = new(host.Registry, host.Manager, host.Options);
        Assert.Null(themes.Error);
        using IDisposable notification = host.Registry.RegisterMenuItems("example.other", Array.Empty<ExtensionMenuContribution>());
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.NotNull(themes.Error);
        Assert.Equal(ThemeVariant.Dark, host.Application.ActualThemeVariant);
        Assert.Equal(host.Options.Current.EditorTheme, themes.Selection);
    }

    private static string CopyProvider(string source)
    {
        string directory = Path.Combine(Path.GetTempPath(), "xve-editor-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, Path.GetFileName(source));
        File.Copy(source, target);
        return target;
    }

    private static Color BrushColor(IResourceHost host, string key)
    {
        ThemeVariant? variant = (host as IThemeVariantHost)?.ActualThemeVariant;
        Assert.True(host.TryFindResource(key, variant, out object? value), key);
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class ThemeHost : IDisposable
    {
        private readonly IResourceDictionary _resources;
        private readonly ThemeVariant? _variant;
        private readonly Avalonia.Themes.Fluent.FluentTheme _fluent = new();
        private readonly DockFluentTheme _dock = new();
        private readonly Avalonia.Markup.Xaml.Styling.StyleInclude _editor = new(new Uri("avares://XamlVisualEditor.App/"))
        {
            Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorTheme.axaml")
        };
        public Application Application { get; } = Avalonia.Application.Current!;
        public ExtensionContributionRegistry Registry { get; } = new();
        public LifecycleManager Manager { get; } = new();
        public EditorOptionsStore Options { get; }
        public string OptionsPath { get; } = Path.Combine(Path.GetTempPath(), "xve-editor-options-" + Guid.NewGuid().ToString("N") + ".json");

        public ThemeHost()
        {
            _resources = Application.Resources;
            _variant = Application.RequestedThemeVariant;
            Application.Resources = new ResourceDictionary();
            Application.RequestedThemeVariant = ThemeVariant.Dark;
            Application.Styles.Add(_fluent);
            Application.Styles.Add(_dock);
            Application.Styles.Add(_editor);
            Options = new EditorOptionsStore(OptionsPath);
        }

        public void Dispose()
        {
            Application.Resources = _resources;
            Application.RequestedThemeVariant = _variant;
            Application.Styles.Remove(_editor);
            Application.Styles.Remove(_dock);
            Application.Styles.Remove(_fluent);
        }
    }

    private sealed class LifecycleManager : IExtensionManager, IExtensionThemeProviderLifecycle
    {
        public event Func<string, CancellationToken, Task>? ThemeProvidersChanging;
        public event Func<string, CancellationToken, Task>? ThemeProvidersChanged;
        public async Task ChangeAsync(string id, Action change)
        {
            if (ThemeProvidersChanging is { } before)
                foreach (Func<string, CancellationToken, Task> handler in before.GetInvocationList()) await handler(id, CancellationToken.None);
            change();
            if (ThemeProvidersChanged is { } after)
                foreach (Func<string, CancellationToken, Task> handler in after.GetInvocationList()) await handler(id, CancellationToken.None);
        }
        public Task<IReadOnlyList<ExtensionPackageInfo>> GetInstalledAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExtensionPackageInfo>>(Array.Empty<ExtensionPackageInfo>());
        public Task<ExtensionPackageInfo> InstallAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task UninstallAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> GetEnabledAsync(string id, CancellationToken ct) => Task.FromResult(true);
        public Task SetEnabledAsync(string id, bool enabled, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExtensionUpdateInfo>> CheckForUpdatesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExtensionUpdateInfo>>(Array.Empty<ExtensionUpdateInfo>());
    }
}

public class SampleEditorThemeProvider : IEditorThemeProvider
{
    public IReadOnlyList<EditorTheme> GetThemes() => new[] { new EditorTheme("violet", "Violet", false), new EditorTheme("broken", "Broken", false) };
    protected virtual string Accent => "#6840B8";
    public EditorThemePalette GetPalette(string themeId)
    {
        Dictionary<EditorThemeColor, string> colors = Enum.GetValues<EditorThemeColor>().ToDictionary(role => role, _ => "#F2EDF9");
        colors[EditorThemeColor.Foreground] = "#211631";
        colors[EditorThemeColor.Accent] = Accent;
        colors[EditorThemeColor.Selection] = Accent;
        colors[EditorThemeColor.AccentForeground] = "#FFFFFF";
        colors[EditorThemeColor.SelectionForeground] = "#FFFFFF";
        if (themeId == "broken") colors[EditorThemeColor.Background] = "invalid-color";
        return new EditorThemePalette(false, colors);
    }
}

public sealed class UpdatedEditorThemeProvider : SampleEditorThemeProvider
{
    protected override string Accent => "#118844";
}

public sealed class ThrowingDisposeEditorThemeProvider : SampleEditorThemeProvider, IDisposable
{
    public void Dispose() => throw new InvalidOperationException("Fixture dispose failure.");
}

public sealed class InvalidCatalogEditorThemeProvider : IEditorThemeProvider
{
    private int _reads;
    public IReadOnlyList<EditorTheme> GetThemes() => ++_reads == 1
        ? new[] { new EditorTheme("violet", "Violet", false) }
        : new[] { new EditorTheme("violet", "Violet", false), new EditorTheme("violet", "Duplicate", false) };
    public EditorThemePalette GetPalette(string id) => new SampleEditorThemeProvider().GetPalette(id);
}
