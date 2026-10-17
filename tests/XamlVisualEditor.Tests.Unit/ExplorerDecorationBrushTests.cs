using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ExplorerDecorationBrushTests
{
    private static readonly string[] Kinds = ["Modified", "Added", "Deleted", "Conflicted"];

    /// <summary>
    /// The decoration colours stay readable on the window background and on a selected row in both theme variants;
    /// 3:1 is the WCAG minimum for large text and user interface components.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Decoration_brushes_have_a_contrast_of_at_least_3_to_1_on_the_background_and_a_selected_row(string variantName)
    {
        ThemeVariant variant = variantName == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        Uri baseUri = new("avares://XamlVisualEditor.App/");
        Border host = new();
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        host.Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml") });
        host.Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorTheme.axaml") });

        // App.axaml paints the window with SystemAltHighColor; the DataGrid paints a selected row with the accent
        // colour at the opacity of its theme over that background.
        Color background = Assert.IsType<Color>(Find(host, "SystemAltHighColor", variant));
        Color accent = Assert.IsType<Color>(Find(host, "SystemAccentColor", variant));
        double opacity = Find(host, "DataGridRowSelectedBackgroundOpacity", variant) is double value ? value : 1;
        Color selection = Blend(accent, opacity, background);
        List<string> measured = [$"{variantName}: background {background}, selected row {selection} (accent {accent} at {opacity})"];

        foreach (string kind in Kinds)
        {
            Color color = Assert.IsType<SolidColorBrush>(Find(host, $"ExplorerDecoration{kind}Brush", variant)).Color;
            double onBackground = Contrast(color, background);
            double onSelection = Contrast(color, selection);
            measured.Add($"{kind} {color}: {onBackground:F2} on background, {onSelection:F2} on selected row");
            Assert.True(onBackground >= 3 && onSelection >= 3, string.Join(Environment.NewLine, measured));
        }

        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, measured));
    }

    private static object? Find(Border host, string key, ThemeVariant variant) =>
        host.TryFindResource(key, variant, out object? value) ? value : null;

    private static Color Blend(Color color, double opacity, Color background)
    {
        double alpha = color.A / 255.0 * opacity;
        byte Mix(byte front, byte back) => (byte)Math.Round(front * alpha + back * (1 - alpha));
        return Color.FromRgb(Mix(color.R, background.R), Mix(color.G, background.G), Mix(color.B, background.B));
    }

    private static double Contrast(Color first, Color second)
    {
        double a = Luminance(first);
        double b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}
