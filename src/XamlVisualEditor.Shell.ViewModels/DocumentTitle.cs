using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Builds the tab title of a document with its modified and read-only markers.</summary>
internal static class DocumentTitle
{
    public static string Format(string fileName, bool isModified, bool isReadOnly)
    {
        string title = isModified ? fileName + "*" : fileName;
        return isReadOnly ? $"{title} {UiStrings.ReadOnlyTitleSuffix}" : title;
    }
}
