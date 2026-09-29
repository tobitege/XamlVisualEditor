using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.App.Services;

/// <summary>Translates library-neutral palettes into the resources of the existing editor controls.</summary>
internal static class EditorThemeResources
{
    private static readonly (EditorThemeColor Role, string[] Keys)[] BrushGroups =
    {
        (EditorThemeColor.Background, new[] { "ThemeBackgroundBrush", "SystemControlBackgroundAltHighBrush", "SystemControlBackgroundChromeLowBrush", "DockThemeBackgroundBrush", "DockSurfaceWorkbenchBrush", "DockSurfaceEditorBrush", "DockWindowChromeBackgroundBrush", "DataGridBackground", "EditorCanvasBackdropBrush" }),
        (EditorThemeColor.Surface, new[] { "SystemControlBackgroundChromeMediumBrush", "SystemControlBackgroundChromeMediumLowBrush", "SystemControlBackgroundBaseLowBrush", "DockThemeControlBackgroundBrush", "DockSurfacePanelBrush", "DockSurfaceSidebarBrush", "DockSurfaceHeaderBrush", "DockDocumentTabStripBackgroundBrush", "DockTabBackgroundBrush", "DockWindowChromeTitleBarBackgroundBrush", "MenuFlyoutPresenterBackground", "ToolTipBackground", "TextControlBackground", "ComboBoxBackground", "DataGridRowBackgroundBrush", "DataGridColumnHeaderBackgroundBrush" }),
        (EditorThemeColor.SurfaceAlt, new[] { "ButtonBackground", "ButtonBackgroundDisabled", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed", "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPressed", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed", "DockTabHoverBackgroundBrush", "DockTabCloseHoverBackgroundBrush", "DockChromeButtonHoverBackgroundBrush", "DockChromeButtonPressedBackgroundBrush", "DataGridRowHoveredBackgroundBrush", "DataGridCellHoveredBackgroundBrush" }),
        (EditorThemeColor.Foreground, new[] { "ThemeForegroundBrush", "SystemControlForegroundBaseHighBrush", "SystemControlForegroundAltHighBrush", "SystemControlPageTextBaseHighBrush", "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed", "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused", "ComboBoxForeground", "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed", "MenuFlyoutItemKeyboardAcceleratorTextForeground", "MenuFlyoutSubItemChevron", "MenuFlyoutSubItemChevronPointerOver", "MenuFlyoutSubItemChevronSubMenuOpened", "ToolTipForeground", "DockThemeForegroundBrush", "DockWindowChromeForegroundBrush", "DockTabForegroundBrush", "DockDocumentTabPointerOverForegroundBrush", "DockChromeButtonForegroundBrush", "DockToolChromeIconBrush", "DataGridColumnHeaderForegroundBrush" }),
        (EditorThemeColor.MutedForeground, new[] { "SystemControlForegroundBaseMediumBrush", "SystemControlDisabledBaseMediumLowBrush", "ButtonForegroundDisabled", "TextControlForegroundDisabled", "ComboBoxForegroundDisabled", "MenuFlyoutItemForegroundDisabled", "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", "MenuFlyoutSubItemChevronDisabled", "DockChromeGripBrush" }),
        (EditorThemeColor.Border, new[] { "ThemeBorderLowBrush", "ThemeBorderMidBrush", "SystemControlForegroundBaseLowBrush", "SystemControlForegroundChromeHighBrush", "ButtonBorderBrush", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "ComboBoxBorderBrush", "MenuFlyoutPresenterBorderBrush", "ToolTipBorderBrush", "DockThemeBorderLowBrush", "DockBorderStrongBrush", "DockBorderSubtleBrush", "DockDocumentContentBorderBrush", "DockSeparatorBrush", "DockSplitterIdleBrush", "DockWindowChromeBorderBrush", "DataGridGridLinesBrush", "DataGridColumnHeaderSeparatorBrush", "EditorStatusBarSeparatorBrush" }),
        (EditorThemeColor.Accent, new[] { "SystemControlHighlightAccentBrush", "SystemControlHighlightListAccentHighBrush", "SystemControlHighlightListAccentMediumBrush", "SystemControlHighlightListAccentLowBrush", "TextControlBorderBrushFocused", "DockThemeAccentBrush", "DockTabActiveIndicatorBrush", "DockSplitterHoverBrush", "DockSplitterDragBrush", "DockTargetIndicatorBrush", "EditorStatusBarBackgroundBrush" }),
        (EditorThemeColor.AccentForeground, new[] { "SystemControlForegroundChromeWhiteBrush", "DockApplicationAccentForegroundBrush", "DockDocumentTabCloseSelectedForegroundBrush", "EditorStatusBarForegroundBrush" }),
        (EditorThemeColor.Selection, new[] { "SystemControlHighlightListLowBrush", "SystemControlHighlightListMediumBrush", "DockTabActiveBackgroundBrush", "DockSurfaceHeaderActiveBrush", "DataGridRowSelectedBackgroundBrush", "DataGridRowSelectedHoveredBackgroundBrush", "DataGridCellSelectedBackgroundBrush", "DataGridCellSelectedHoveredBackgroundBrush", "DataGridRowSelectedUnfocusedBackgroundBrush", "DataGridCellSelectedUnfocusedBackgroundBrush", "DataGridRowSelectedUnfocusedHoveredBackgroundBrush", "DataGridCellSelectedUnfocusedHoveredBackgroundBrush" }),
        (EditorThemeColor.SelectionForeground, new[] { "SystemControlHighlightAltBaseHighBrush", "DockTabActiveForegroundBrush", "DataGridRowSelectedForegroundBrush", "DataGridCellSelectedForegroundBrush" }),
        // Selected but inactive dock tabs sit on the panel/header surface.
        (EditorThemeColor.Foreground, new[] { "DockTabSelectedForegroundBrush", "DockDocumentTabSelectedForegroundBrush" }),
        (EditorThemeColor.Error, new[] { "DockChromeButtonDangerHoverBrush", "DockDocumentTabClosePointerOverForegroundBrush", "DataGridCellInvalidBrush", "EditorErrorBrush" }),
        (EditorThemeColor.Warning, new[] { "DataGridCellWarningBrush", "EditorWarningBrush" }),
        (EditorThemeColor.Success, new[] { "EditorSuccessBrush" })
    };

    private static readonly (EditorThemeColor Role, string[] Keys)[] ColorGroups =
    {
        (EditorThemeColor.Background, new[] { "SystemAltHighColor", "SystemRegionColor", "SystemChromeLowColor" }),
        (EditorThemeColor.Surface, new[] { "SystemAltMediumColor", "SystemAltMediumHighColor", "SystemAltMediumLowColor", "SystemAltLowColor", "SystemChromeMediumColor", "SystemChromeMediumLowColor", "SystemChromeAltLowColor", "SystemChromeBlackLowColor", "SystemChromeBlackMediumLowColor" }),
        (EditorThemeColor.SurfaceAlt, new[] { "SystemChromeHighColor", "SystemChromeGrayColor", "SystemChromeBlackHighColor", "SystemChromeBlackMediumColor", "SystemChromeWhiteColor", "SystemChromeDisabledHighColor", "SystemChromeDisabledLowColor", "SystemListLowColor", "SystemListMediumColor", "SystemRevealListLowColor", "SystemRevealListMediumColor" }),
        (EditorThemeColor.Foreground, new[] { "SystemBaseHighColor", "SystemBaseMediumHighColor" }),
        (EditorThemeColor.MutedForeground, new[] { "SystemBaseMediumColor", "SystemBaseMediumLowColor", "SystemBaseLowColor" }),
        (EditorThemeColor.Accent, new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" }),
        (EditorThemeColor.Error, new[] { "SystemErrorTextColor" })
    };

    private static IEnumerable<string> Keys => BrushGroups.SelectMany(group => group.Keys)
        .Concat(ColorGroups.SelectMany(group => group.Keys)).Concat(AccentForegroundKeys)
        .Concat(new[] { "EditorBusyBackdropBrush", "EditorWindowBackgroundBrush", "EditorWindowForegroundBrush" }).Distinct(StringComparer.Ordinal);

    private static readonly string[] AccentForegroundKeys =
    {
        "ToggleButtonForegroundChecked", "ToggleButtonForegroundCheckedPointerOver", "ToggleButtonForegroundCheckedPressed",
        "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed"
    };

    public static ResourceDictionary Create(EditorThemePalette palette)
    {
        Dictionary<EditorThemeColor, Color> colors = new();
        foreach (EditorThemeColor role in Enum.GetValues<EditorThemeColor>())
        {
            if (!palette.Colors.TryGetValue(role, out string? value) || !Color.TryParse(value, out Color color))
                throw new InvalidOperationException("Missing or invalid editor theme color: " + role);
            colors.Add(role, color);
        }
        ResourceDictionary resources = new();
        resources["EditorWindowBackgroundBrush"] = new SolidColorBrush(colors[EditorThemeColor.Background]);
        resources["EditorWindowForegroundBrush"] = new SolidColorBrush(colors[EditorThemeColor.Foreground]);
        foreach (string key in AccentForegroundKeys) resources[key] = new SolidColorBrush(colors[EditorThemeColor.AccentForeground]);
        foreach (var group in BrushGroups)
            foreach (string key in group.Keys) resources[key] = new SolidColorBrush(colors[group.Role]);
        foreach (var group in ColorGroups)
            foreach (string key in group.Keys) resources[key] = colors[group.Role];
        Color background = colors[EditorThemeColor.Background];
        resources["EditorBusyBackdropBrush"] = new SolidColorBrush(Color.FromArgb(208, background.R, background.G, background.B));
        Color button = colors[EditorThemeColor.SurfaceAlt];
        Color foreground = colors[EditorThemeColor.Foreground];
        Color accent = colors[EditorThemeColor.Accent];
        Color accentForeground = colors[EditorThemeColor.AccentForeground];
        resources["EditorDialogButtonBackgroundBrush"] = new SolidColorBrush(button);
        resources["EditorDialogButtonHoverBrush"] = new SolidColorBrush(Blend(button, foreground, 0.12));
        resources["EditorDialogButtonPressedBrush"] = new SolidColorBrush(Blend(button, background, 0.35));
        resources["EditorDialogButtonForegroundBrush"] = new SolidColorBrush(foreground);
        resources["EditorDialogButtonBorderBrush"] = new SolidColorBrush(Blend(button, foreground, 0.35));
        resources["EditorDialogButtonDisabledBackgroundBrush"] = new SolidColorBrush(colors[EditorThemeColor.Surface]);
        resources["EditorDialogButtonDisabledForegroundBrush"] = new SolidColorBrush(colors[EditorThemeColor.MutedForeground]);
        resources["EditorDialogButtonDisabledBorderBrush"] = new SolidColorBrush(colors[EditorThemeColor.Border]);
        resources["EditorDialogPrimaryBrush"] = new SolidColorBrush(accent);
        resources["EditorDialogPrimaryHoverBrush"] = new SolidColorBrush(Blend(accent, accentForeground, 0.1));
        resources["EditorDialogPrimaryPressedBrush"] = new SolidColorBrush(Blend(accent, background, 0.2));
        resources["EditorDialogPrimaryForegroundBrush"] = new SolidColorBrush(accentForeground);
        return resources;
    }

    private static Color Blend(Color color, Color overlay, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R + (overlay.R - color.R) * amount),
        (byte)Math.Round(color.G + (overlay.G - color.G) * amount),
        (byte)Math.Round(color.B + (overlay.B - color.B) * amount));

    public static IReadOnlyDictionary<ThemeVariant, IReadOnlyDictionary<string, object>> CaptureDefaults(Application application)
    {
        Dictionary<ThemeVariant, IReadOnlyDictionary<string, object>> variants = new();
        foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Dictionary<string, object> values = new(StringComparer.Ordinal);
            foreach (string key in Keys)
                if (application.TryGetResource(key, variant, out object? value) && value is not null)
                    values[key] = value is ISolidColorBrush brush ? new SolidColorBrush(brush.Color, brush.Opacity) : value;
            variants.Add(variant, values);
        }
        return variants;
    }

    public static ResourceDictionary CreatePreviewDefaults(IReadOnlyDictionary<ThemeVariant, IReadOnlyDictionary<string, object>> defaults)
    {
        ResourceDictionary resources = new();
        foreach (var variant in defaults)
        {
            ResourceDictionary colors = new();
            foreach (var item in variant.Value) colors[item.Key] = item.Value;
            resources.ThemeDictionaries[variant.Key] = colors;
        }
        return resources;
    }
}
