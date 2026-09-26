using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Builds the tab title of a document with its modified and read-only markers, and the window title.</summary>
internal static class DocumentTitle
{
    public static string Format(string fileName, bool isModified, bool isReadOnly)
    {
        string title = isModified ? fileName + "*" : fileName;
        return isReadOnly ? $"{title} {UiStrings.ReadOnlyTitleSuffix}" : title;
    }

    /// <summary>Builds the window title from the active document: its file name, or its full path on request.</summary>
    public static string FormatWindow(string? fileName, string? filePath, bool showFullPath)
    {
        string? document = showFullPath && !string.IsNullOrWhiteSpace(filePath) ? filePath : fileName;
        return string.IsNullOrWhiteSpace(document) ? UiStrings.ApplicationName : $"{UiStrings.ApplicationName} — {document}";
    }
}
