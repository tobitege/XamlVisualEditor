using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Flowery;
using Flowery.Controls;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.Core;
using XamlVisualEditor.FloweryThemeExtension;
using XamlVisualEditor.TestControls;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class FloweryThemeProviderTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisteredPalettesUseAnIsolatedOverlayAndRestoreProjectDefault(bool existingStyle)
    {
        Application application = Application.Current!;
        IResourceDictionary originalResources = application.Resources;
        ThemeVariant? originalVariant = application.RequestedThemeVariant;
        string? originalTheme = DaisyThemeManager.CurrentThemeName;
        var originalExclusions = DaisyThemeManager.ExcludedThemes;
        var originalPreferences = DaisyThemeManager.PreferredThemes;
        var originalApplicator = DaisyThemeManager.CustomThemeApplicator;
        Styles? nestedStyle = existingStyle ? new Styles { new DaisyUITheme() } : null;
        List<string> themeChanges = new();
        void RecordThemeChange(object? sender, string themeName) => themeChanges.Add(themeName);
        try
        {
            // The provider records the application state when it applies its first palette, not when it is created.
            using FloweryThemeProvider provider = new();
            ResourceDictionary baseline = new() { ["PreviewAccent"] = new SolidColorBrush(Color.Parse("#334455")) };
            baseline.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary
            {
                ["PreviewAccent"] = new SolidColorBrush(Color.Parse("#556677"))
            };
            application.Resources = baseline;
            application.RequestedThemeVariant = ThemeVariant.Light;
            string? baselineName = existingStyle ? "Light" : null;
            // Flowery.NET offers no other way to reach the unset theme name of a freshly started application.
            DaisyThemeManager.SetCurrentTheme(baselineName!);
            if (nestedStyle is not null) application.Styles.Add(nestedStyle);
            IStyle[] baselineStyles = application.Styles.ToArray();
            int customApplications = 0;
            DaisyThemeManager.CustomThemeApplicator = _ => { customApplications++; return true; };
            DaisyThemeManager.RegisterTheme(new DaisyThemeInfo("XvePreviewNight", true),
                () => new ResourceDictionary { ["PreviewAccent"] = new SolidColorBrush(Color.Parse("#123456")) });
            DaisyThemeManager.RegisterTheme(new DaisyThemeInfo("XvePreviewDay", false),
                () => new ResourceDictionary { ["PreviewAccent"] = new SolidColorBrush(Color.Parse("#C08050")) });
            DaisyThemeManager.ExcludedThemes = new[] { "Dark" };
            DaisyThemeManager.PreferredThemes = new[] { "XvePreviewNight" };
            DaisyThemeManager.ThemeChanged += RecordThemeChange;

            var catalog = provider.GetThemes();
            Assert.Equal("xvepreviewnight", catalog[0].Id);
            Assert.True(Assert.Single(catalog, theme => theme.Id == "xvepreviewnight").IsDark);
            Assert.DoesNotContain(catalog, theme => theme.Id == "dark");
            provider.ApplyTheme(null);
            Assert.Same(baseline, application.Resources);
            Assert.Equal(baselineName, DaisyThemeManager.CurrentThemeName);
            Assert.Empty(themeChanges);

            provider.ApplyTheme("xvepreviewnight");
            Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);
            Assert.Equal("XvePreviewNight", DaisyThemeManager.CurrentThemeName);
            Assert.Equal(new[] { "XvePreviewNight" }, themeChanges);
            Assert.Equal(Color.Parse("#123456"), Accent(application.Resources, ThemeVariant.Dark));
            Assert.Equal(Color.Parse("#334455"), Accent(baseline, ThemeVariant.Dark));
            Assert.Equal(Color.Parse("#556677"), Accent(
                Assert.IsType<ResourceDictionary>(baseline.ThemeDictionaries[ThemeVariant.Dark]), ThemeVariant.Dark));
            Assert.Equal(baselineStyles.Length + (existingStyle ? 0 : 1), application.Styles.Count);
            IResourceDictionary activeResources = application.Resources;
            Assert.Throws<ArgumentException>(() => provider.ApplyTheme("unregistered-preview-theme"));
            Assert.Same(activeResources, application.Resources);

            DaisyThemeManager.RegisterTheme(new DaisyThemeInfo("XvePreviewBroken", false),
                () => throw new InvalidOperationException("Fixture palette failure."));
            Assert.Contains(provider.GetThemes(), theme => theme.Id == "xvepreviewbroken");
            Assert.Throws<InvalidOperationException>(() => provider.ApplyTheme("xvepreviewbroken"));
            Assert.Same(activeResources, application.Resources);
            Assert.Equal(Color.Parse("#123456"), Accent(application.Resources, ThemeVariant.Dark));

            provider.ApplyTheme("xvepreviewday");
            Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);
            Assert.Equal(Color.Parse("#C08050"), Accent(application.Resources, ThemeVariant.Light));
            themeChanges.Clear();
            provider.ApplyTheme(null);
            Assert.Same(baseline, application.Resources);
            Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);
            Assert.Equal(baselineStyles, application.Styles.ToArray());
            Assert.Equal(0, customApplications);
            if (baselineName is null)
            {
                // An unset original name is not forced back; the name of the last preview theme remains.
                Assert.Equal("XvePreviewDay", DaisyThemeManager.CurrentThemeName);
                Assert.Empty(themeChanges);
            }
            else
            {
                Assert.Equal(baselineName, DaisyThemeManager.CurrentThemeName);
                Assert.Equal(new[] { baselineName }, themeChanges);
            }

            provider.ApplyTheme("xvepreviewnight");
            ResourceDictionary otherProvider = new();
            application.Resources = otherProvider;
            application.RequestedThemeVariant = ThemeVariant.Light;
            DaisyThemeManager.SetCurrentTheme("Light");
            provider.Dispose();
            Assert.Same(otherProvider, application.Resources);
            Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);
            Assert.Equal("Light", DaisyThemeManager.CurrentThemeName);
        }
        finally
        {
            DaisyThemeManager.ThemeChanged -= RecordThemeChange;
            if (nestedStyle is not null) application.Styles.Remove(nestedStyle);
            application.Resources = originalResources;
            application.RequestedThemeVariant = originalVariant;
            DaisyThemeManager.SetCurrentTheme(originalTheme!);
            DaisyThemeManager.CustomThemeApplicator = originalApplicator;
            DaisyThemeManager.ExcludedThemes = originalExclusions.Concat(new[] { "XvePreviewNight", "XvePreviewDay", "XvePreviewBroken" }).ToArray();
            DaisyThemeManager.PreferredThemes = originalPreferences;
        }
    }

    [AvaloniaFact]
    public void ProjectsWithoutFloweryReceiveACatalogErrorAndKeepTheirTheme()
    {
        Application application = Application.Current!;
        IResourceDictionary resources = application.Resources;
        ThemeVariant? variant = application.RequestedThemeVariant;
        IStyle[] styles = application.Styles.ToArray();
        string? themeName = DaisyThemeManager.CurrentThemeName;
        // The editor contract, Avalonia, the extension and Flowery.NET itself do not make a project a Flowery project.
        Assembly[] withoutFlowery =
        {
            typeof(Application).Assembly, typeof(DesignerTheme).Assembly,
            typeof(FloweryThemeProvider).Assembly, typeof(DaisyThemeManager).Assembly
        };
        Assert.False(FloweryThemeProvider.ReferencesFlowery(withoutFlowery));
        Assert.True(FloweryThemeProvider.ReferencesFlowery(withoutFlowery.Append(typeof(FloweryFixtureControl).Assembly)));

        using FloweryThemeProvider provider = new(() => withoutFlowery, () => new Version(3, 4, 0, 0));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => provider.GetThemes());
        Assert.Equal("The project does not reference Flowery.NET.", error.Message);
        Assert.Throws<InvalidOperationException>(() => provider.ApplyTheme("light"));
        provider.ApplyTheme(null);
        Assert.Same(resources, application.Resources);
        Assert.Equal(variant, application.RequestedThemeVariant);
        Assert.Equal(styles, application.Styles.ToArray());
        Assert.Equal(themeName, DaisyThemeManager.CurrentThemeName);
        AssertDerivedThemeFallsBack(error.Message);
    }

    [AvaloniaTheory]
    [InlineData("3.1.1.0", false)]
    [InlineData(null, false)]
    [InlineData("3.2.0.0", true)]
    public void FloweryVersionsWithoutPaletteFactoriesReceiveACatalogError(string? version, bool supported)
    {
        Application application = Application.Current!;
        IResourceDictionary resources = application.Resources;
        IStyle[] styles = application.Styles.ToArray();
        using FloweryThemeProvider provider = new(() => new[] { typeof(FloweryFixtureControl).Assembly },
            () => version is null ? null : Version.Parse(version));
        if (supported)
        {
            Assert.Contains(provider.GetThemes(), theme => theme.Id == "light");
            return;
        }
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => provider.GetThemes());
        Assert.Contains(FloweryThemeProvider.MinimumFloweryVersion.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => provider.ApplyTheme("light"));
        Assert.Same(resources, application.Resources);
        Assert.Equal(styles, application.Styles.ToArray());
        AssertDerivedThemeFallsBack(error.Message);
    }

    [AvaloniaFact]
    public void ThemesTheTargetApplicationExcludesAreNotDerivedFromTheEditorTheme()
    {
        var originalExclusions = DaisyThemeManager.ExcludedThemes;
        try
        {
            DaisyThemeManager.ExcludedThemes = originalExclusions.Concat(new[] { "Dark", "Forest" }).ToArray();
            using FloweryThemeProvider provider = new();
            DesignerThemeProviderRegistration registration = FloweryRegistration();
            DesignerThemeCatalog catalog = new(Guid.NewGuid(), new[]
            {
                new DesignerThemeProviderCatalog(registration.Id, registration.Name, provider.GetThemes())
            });
            DesignerThemeSelection? Derive(EditorThemeSelection editorTheme) => EditorPreviewThemeDefaults.Resolve(
                editorTheme, editorTheme.ThemeId == "dark", new[] { registration }, FloweryProject(), catalog);

            Assert.Null(Derive(new(EditorPreviewThemeDefaults.BuiltinProviderId, "dark")));
            Assert.Null(Derive(new(registration.Id, "forest")));
            Assert.Equal(new DesignerThemeSelection(registration.Id, "light"), Derive(new(EditorPreviewThemeDefaults.BuiltinProviderId, "light")));
            Assert.Equal(new DesignerThemeSelection(registration.Id, "cupcake"), Derive(new(registration.Id, "cupcake")));
        }
        finally
        {
            DaisyThemeManager.ExcludedThemes = originalExclusions;
        }
    }

    [AvaloniaFact]
    public void EditorPalettesSupplyEveryEditorColorForEveryAvailableTheme()
    {
        FloweryEditorThemeProvider provider = new();
        IReadOnlyList<EditorTheme> themes = provider.GetThemes();
        Assert.Contains(themes, theme => theme.Id == "forest");
        foreach (EditorTheme theme in themes)
        {
            EditorThemePalette palette = provider.GetPalette(theme.Id);
            Assert.Equal(theme.IsDark, palette.IsDark);
            Assert.Equal(Enum.GetValues<EditorThemeColor>(), palette.Colors.Keys.Order());
            foreach (string value in palette.Colors.Values)
                Assert.True(Color.TryParse(value, out _), theme.Id + ": " + value);
            ResourceDictionary source = DaisyThemeManager.GetPaletteFactory(theme.Name)!.Invoke();
            Color primaryContent = (Color)source["DaisyPrimaryContentColor"]!;
            Assert.Equal(primaryContent, Color.Parse(palette.Colors[EditorThemeColor.AccentForeground]));
            Assert.Equal(primaryContent, Color.Parse(palette.Colors[EditorThemeColor.SelectionForeground]));
            // Borders stay visible on SurfaceAlt and blend like every other opaque role.
            Color border = Color.Parse(palette.Colors[EditorThemeColor.Border]);
            Assert.NotEqual(Color.Parse(palette.Colors[EditorThemeColor.SurfaceAlt]), border);
            Assert.Equal(255, border.A);
        }
    }

    [AvaloniaFact]
    public void FloweryEditorPalettesDoNotChangeApplicationOrPreviewThemeState()
    {
        Application application = Application.Current!;
        IResourceDictionary resources = application.Resources;
        ThemeVariant? variant = application.RequestedThemeVariant;
        string? selectedTheme = DaisyThemeManager.CurrentThemeName;
        FloweryEditorThemeProvider provider = new();
        Assert.Contains(provider.GetThemes(), theme => theme.Id == "light");
        Assert.Contains(provider.GetThemes(), theme => theme.Id == "dark");
        EditorThemePalette dark = provider.GetPalette("dark");
        EditorThemePalette light = provider.GetPalette("light");
        EditorThemePalette forest = provider.GetPalette("forest");
        ResourceDictionary originalForest = DaisyThemeManager.GetPaletteFactory("Forest")!.Invoke();
        Assert.Equal(originalForest["DaisyPrimaryContentColor"], Color.Parse(forest.Colors[EditorThemeColor.AccentForeground]));
        Assert.Equal(originalForest["DaisyPrimaryContentColor"], Color.Parse(forest.Colors[EditorThemeColor.SelectionForeground]));
        Assert.Equal(originalForest["DaisyPrimaryColor"], Color.Parse(forest.Colors[EditorThemeColor.Accent]));
        Assert.True(dark.IsDark);
        Assert.False(light.IsDark);
        Assert.Equal(Enum.GetValues<EditorThemeColor>().Length, dark.Colors.Count);
        Assert.NotEqual(dark.Colors[EditorThemeColor.Background], light.Colors[EditorThemeColor.Background]);
        Assert.Same(resources, application.Resources);
        Assert.Equal(variant, application.RequestedThemeVariant);
        Assert.Equal(selectedTheme, DaisyThemeManager.CurrentThemeName);
        Assert.Throws<ArgumentException>(() => provider.GetPalette("not-a-theme"));
        Assert.Same(resources, application.Resources);
    }

    [AvaloniaFact]
    public void InstalledFloweryProviderLoadsItsPrivatePaletteResourcesWithoutChangingGlobalDaisyState()
    {
        string providerPath = CopyEditorProvider();
        try
        {
            using EditorThemeTests.ThemeHost host = new();
            using IDisposable registration = host.Registry.RegisterEditorThemeProviders("example.flowery", new[]
            {
                new EditorThemeProviderRegistration("example.flowery/palette", "Flowery.NET", providerPath, typeof(FloweryEditorThemeProvider).FullName!)
            });
            string? previewTheme = DaisyThemeManager.CurrentThemeName;
            using EditorThemeService themes = new(host.Registry, host.Manager, host.Options);
            EditorThemeProviderCatalog catalog = Assert.Single(themes.Catalogs, item => item.Id == "example.flowery/palette");
            Assert.Null(catalog.Error);
            Assert.Contains(catalog.Themes, theme => theme.Id == "dark");
            themes.Apply(new(catalog.Id, "dark"));
            Color dark = EditorThemeTests.BrushColor(host.Application, "DockSurfaceWorkbenchBrush");
            themes.Apply(new(catalog.Id, "light"));
            Color light = EditorThemeTests.BrushColor(host.Application, "DockSurfaceWorkbenchBrush");
            Assert.NotEqual(dark, light);
            Assert.Equal(previewTheme, DaisyThemeManager.CurrentThemeName);
            Assert.Equal(ThemeVariant.Light, host.Application.ActualThemeVariant);

            ToggleButton button = new() { Content = "Selected", IsChecked = true };
            Window window = new() { Content = button, Width = 300, Height = 150 };
            window.Show();
            try
            {
                themes.Apply(new(catalog.Id, "cupcake"));
                EditorThemeTests.Flush(window);
                EditorThemePalette palette = new FloweryEditorThemeProvider().GetPalette("cupcake");
                Assert.Equal(Color.Parse(palette.Colors[EditorThemeColor.Background]), Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
                TextBlock caption = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>(), item => item.Text == "Selected");
                Assert.Equal(Color.Parse(palette.Colors[EditorThemeColor.AccentForeground]), Assert.IsAssignableFrom<ISolidColorBrush>(caption.Foreground).Color);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(Path.GetDirectoryName(providerPath)!, recursive: true); }
    }

    [AvaloniaFact]
    public void EditorProviderLoadedIntoItsPrivateContextReadsTheSamePalettes()
    {
        string providerPath = CopyEditorProvider();
        try
        {
            FloweryEditorThemeProvider direct = new();
            using EditorThemeProviderLoader loader = new(EditorRegistration(providerPath));

            IReadOnlyList<EditorTheme> themes = loader.GetThemes();
            Assert.Contains(themes, theme => theme.Id == "forest");
            // The private context resolves the Flowery.NET copy next to the provider instead of the editor's.
            Assert.Contains(loader.Assemblies, assembly => assembly.GetName().Name == "Flowery.NET");
            foreach (EditorTheme theme in themes)
            {
                EditorThemePalette expected = direct.GetPalette(theme.Id);
                EditorThemePalette actual = loader.GetPalette(theme.Id);
                Assert.Equal(expected.IsDark, actual.IsDark);
                Assert.Equal(expected.Colors.OrderBy(item => item.Key), actual.Colors.OrderBy(item => item.Key));
            }
        }
        finally { Directory.Delete(Path.GetDirectoryName(providerPath)!, recursive: true); }
    }

    [AvaloniaFact]
    public void EditorProviderContextUnloadsAfterDispose()
    {
        string providerPath = CopyEditorProvider();
        try
        {
            WeakReference context = LoadReadAndDispose(providerPath);
            for (int attempt = 0; context.IsAlive && attempt < 10; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(context.IsAlive);
        }
        finally { Directory.Delete(Path.GetDirectoryName(providerPath)!, recursive: true); }
    }

    [Fact]
    public void PackageVersionMatchesTheManifestVersion()
    {
        string extensionDirectory = Path.Combine(FindRepoRoot(), "extensions", "XamlVisualEditor.FloweryThemeExtension");
        XDocument project = XDocument.Load(Path.Combine(extensionDirectory, "XamlVisualEditor.FloweryThemeExtension.csproj"));
        string packageVersion = Assert.Single(project.Descendants("VersionPrefix")).Value;
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(extensionDirectory, "xve.extension.json")));
        Assert.Equal(packageVersion, manifest.RootElement.GetProperty("version").GetString());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadReadAndDispose(string providerPath)
    {
        EditorThemeProviderLoader loader = new(EditorRegistration(providerPath));
        Assert.Contains(loader.GetThemes(), theme => theme.Id == "forest");
        Assert.True(loader.GetPalette("forest").IsDark);
        WeakReference context = new(loader);
        loader.Dispose();
        return context;
    }

    private static void AssertDerivedThemeFallsBack(string providerError)
    {
        DesignerThemeProviderRegistration registration = FloweryRegistration();
        DesignerThemeCatalog catalog = new(Guid.NewGuid(), new[]
        {
            new DesignerThemeProviderCatalog(registration.Id, registration.Name, Array.Empty<DesignerTheme>(), providerError)
        });
        Assert.Null(EditorPreviewThemeDefaults.Resolve(new(EditorPreviewThemeDefaults.BuiltinProviderId, "light"), false,
            new[] { registration }, FloweryProject(), catalog));
    }

    private static DesignerThemeProviderRegistration FloweryRegistration() => new("xamlvisualeditor.flowery-themes/palette",
        "Flowery.NET", typeof(FloweryThemeProvider).Assembly.Location, typeof(FloweryThemeProvider).FullName!, "Flowery.NET");

    private static ProjectModel FloweryProject() => new()
    {
        Name = "FloweryApp",
        ProjectPath = Path.Combine(Path.GetTempPath(), "FloweryApp", "FloweryApp.csproj"),
        XamlFiles = Array.Empty<XamlFileModel>(),
        Files = Array.Empty<ProjectFileModel>(),
        References = new[] { new AssemblyReference { Name = "Flowery.NET", Path = typeof(DaisyThemeManager).Assembly.Location } }
    };

    private static EditorThemeProviderRegistration EditorRegistration(string providerPath) => new(
        "xamlvisualeditor.flowery-themes/palette", "Flowery.NET", providerPath, typeof(FloweryEditorThemeProvider).FullName!);

    /// <summary>Copies the extension and Flowery.NET into a new temporary directory, as an installed package lays them out.</summary>
    private static string CopyEditorProvider()
    {
        string directory = Path.Combine(Path.GetTempPath(), "xve-flowery-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string providerPath = Path.Combine(directory, Path.GetFileName(typeof(FloweryEditorThemeProvider).Assembly.Location));
        File.Copy(typeof(FloweryEditorThemeProvider).Assembly.Location, providerPath);
        File.Copy(typeof(DaisyThemeManager).Assembly.Location, Path.Combine(directory, "Flowery.NET.dll"));
        return providerPath;
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "XamlVisualEditor.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Failed to locate the repository root.");
    }

    private static Color Accent(IResourceDictionary resources, ThemeVariant variant)
    {
        Assert.True(resources.TryGetResource("PreviewAccent", variant, out object? value));
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
