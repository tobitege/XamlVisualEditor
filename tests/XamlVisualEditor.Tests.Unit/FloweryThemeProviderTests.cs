using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Flowery;
using Flowery.Controls;
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

    private static Color Accent(IResourceDictionary resources, ThemeVariant variant)
    {
        Assert.True(resources.TryGetResource("PreviewAccent", variant, out object? value));
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
