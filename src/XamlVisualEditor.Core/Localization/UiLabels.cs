namespace XamlVisualEditor.Core.Localization;

public static class UiLabels
{
    public static string Category(string value) => value switch
    {
        "Layout" => UiStrings.Layout,
        "Appearance" => UiStrings.CategoryAppearance,
        "Common" => UiStrings.CategoryCommon,
        "Misc" => UiStrings.CategoryMisc,
        "Miscellaneous" => UiStrings.CategoryMiscellaneous,
        "Custom" => UiStrings.CategoryCustom,
        "Controls" => UiStrings.CategoryControls,
        "Text" => UiStrings.CategoryText,
        "Content" => UiStrings.CategoryContent,
        "Input" => UiStrings.Input,
        "Library content" => UiStrings.CategoryLibraryContent,
        _ => value
    };

    public static string DiagnosticFilter(string value) => value switch
    {
        "All" => UiStrings.FilterAll,
        "None" => UiStrings.FilterNone,
        "File" => UiStrings.File,
        "Channel" => UiStrings.Channel,
        "Error" => UiStrings.SeverityError,
        "Warning" => UiStrings.SeverityWarning,
        "Info" => UiStrings.SeverityInfo,
        _ => value
    };
}
