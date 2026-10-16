using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Flowery;
using Flowery.Controls;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.Core;
using XamlVisualEditor.FloweryThemeExtension;
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
        try
        {
            ResourceDictionary baseline = new() { ["PreviewAccent"] = new SolidColorBrush(Color.Parse("#334455")) };
            baseline.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary
            {
                ["PreviewAccent"] = new SolidColorBrush(Color.Parse("#556677"))
            };
            application.Resources = baseline;
            application.RequestedThemeVariant = ThemeVariant.Light;
            string? baselineName = existingStyle ? "Light" : null;
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

            using FloweryThemeProvider provider = new();
            var catalog = provider.GetThemes();
            Assert.Equal("xvepreviewnight", catalog[0].Id);
            Assert.True(Assert.Single(catalog, theme => theme.Id == "xvepreviewnight").IsDark);
            Assert.DoesNotContain(catalog, theme => theme.Id == "dark");
            provider.ApplyTheme(null);
            Assert.Same(baseline, application.Resources);
            Assert.Equal(baselineName, DaisyThemeManager.CurrentThemeName);

            provider.ApplyTheme("xvepreviewnight");
            Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);
            Assert.Equal("XvePreviewNight", DaisyThemeManager.CurrentThemeName);
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
            provider.ApplyTheme(null);
            Assert.Same(baseline, application.Resources);
            Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);
            Assert.Equal(baselineName, DaisyThemeManager.CurrentThemeName);
            Assert.Equal(baselineStyles, application.Styles.ToArray());
            Assert.Equal(0, customApplications);

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
    public void ThemesTheTargetApplicationExcludesAreNotDerivedFromTheEditorTheme()
    {
        var originalExclusions = DaisyThemeManager.ExcludedThemes;
        try
        {
            DaisyThemeManager.ExcludedThemes = originalExclusions.Concat(new[] { "Dark", "Forest" }).ToArray();
            using FloweryThemeProvider provider = new();
            DesignerThemeProviderRegistration registration = new("xamlvisualeditor.flowery-themes/palette", "Flowery.NET",
                typeof(FloweryThemeProvider).Assembly.Location, typeof(FloweryThemeProvider).FullName!, "Flowery.NET");
            DesignerThemeCatalog catalog = new(Guid.NewGuid(), new[]
            {
                new DesignerThemeProviderCatalog(registration.Id, registration.Name, provider.GetThemes())
            });
            ProjectModel project = new()
            {
                Name = "FloweryApp",
                ProjectPath = Path.Combine(Path.GetTempPath(), "FloweryApp", "FloweryApp.csproj"),
                XamlFiles = Array.Empty<XamlFileModel>(),
                Files = Array.Empty<ProjectFileModel>(),
                References = new[] { new AssemblyReference { Name = "Flowery.NET", Path = typeof(DaisyThemeManager).Assembly.Location } }
            };
            DesignerThemeSelection? Derive(EditorThemeSelection editorTheme) => EditorPreviewThemeDefaults.Resolve(
                editorTheme, editorTheme.ThemeId == "dark", new[] { registration }, project, catalog);

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
        }
    }

    [AvaloniaFact]
    public void EditorProviderLoadedIntoItsPrivateContextReadsTheSamePalettes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "xve-flowery-palettes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string providerPath = Path.Combine(directory, Path.GetFileName(typeof(FloweryEditorThemeProvider).Assembly.Location));
        File.Copy(typeof(FloweryEditorThemeProvider).Assembly.Location, providerPath);
        File.Copy(typeof(DaisyThemeManager).Assembly.Location, Path.Combine(directory, "Flowery.NET.dll"));
        FloweryEditorThemeProvider direct = new();
        using EditorThemeProviderLoader loader = new(new EditorThemeProviderRegistration(
            "xamlvisualeditor.flowery-themes/palette", "Flowery.NET", providerPath, typeof(FloweryEditorThemeProvider).FullName!));

        IReadOnlyList<EditorTheme> themes = loader.GetThemes();
        Assert.Contains(themes, theme => theme.Id == "forest");
        foreach (EditorTheme theme in themes)
        {
            EditorThemePalette expected = direct.GetPalette(theme.Id);
            EditorThemePalette actual = loader.GetPalette(theme.Id);
            Assert.Equal(expected.IsDark, actual.IsDark);
            Assert.Equal(expected.Colors.OrderBy(item => item.Key), actual.Colors.OrderBy(item => item.Key));
        }
    }

    private static Color Accent(IResourceDictionary resources, ThemeVariant variant)
    {
        Assert.True(resources.TryGetResource("PreviewAccent", variant, out object? value));
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
