using System.Collections;
using System.Globalization;
using System.Resources;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Tests.Unit.Extensions;
using XamlVisualEditor.Xaml.Ast;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

[Collection(nameof(UiLocalizationCollection))]
public sealed class UiStringsLocalizationTests
{
    /// <summary>Keys whose German value is the English one on purpose.</summary>
    private static readonly HashSet<string> SameInGerman = new(StringComparer.Ordinal)
    {
        // Git terms stay English in the German user interface.
        "Git", "BranchLabel", "UpstreamLabel", "Stage", "Unstage", "StageAll", "UnstageAll", "Commit",
        "StagedCountFormat", "UnstagedCountFormat", "DiffCountFormat", "GitStaged", "GitUnstaged", "GitUntracked",
        // Agreed English UI terms.
        "CategoryControls", "Toolbox", "Assemblies", "Layout", "StatusLayoutFormat", "LayoutPresetDesigner",
        "LayoutPresetCode", "CodeFormat", "Theme", "Zoom", "ZoomLabel", "Solutions", "Handler",
        // Words that German spells the same way.
        "Name", "Status", "Version", "VersionFormat", "Definition", "Navigation", "Animation", "Transport", "Clients",
        "TerminalGroup", "TerminalShell", "EditorThemeSystem", "LayoutPresetMinimal", "CategoryText", "SeverityInfo",
        "DownloadFormat", "ToolboxErrorDetails", "Ok",
        // Product, protocol and executable names.
        "ApplicationName", "Acp", "Mcp", "Xaml", "WindowsPowerShell", "PowerShell7", "CmdExecutable",
        "WindowsPowerShellExecutable", "PowerShellExecutable",
        // Format and example values.
        "AnimationKeyExample", "ArgbExample", "GridLengthAlternatives", "HttpsExample", "TimeFormatExample",
        "CoordinateStripPointer",
    };

    [Fact]
    public void GermanResources_TranslateEveryNeutralString()
    {
        Dictionary<string, string> neutral = ReadStrings(CultureInfo.InvariantCulture);
        Dictionary<string, string> german = ReadStrings(CultureInfo.GetCultureInfo("de"));

        Assert.Empty(neutral
            .Where(entry => !SameInGerman.Contains(entry.Key)
                && german.TryGetValue(entry.Key, out string? translation)
                && string.Equals(entry.Value, translation, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .OrderBy(key => key, StringComparer.Ordinal));
        Assert.Empty(SameInGerman.Where(key => !neutral.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void LayoutRefusalsAreReportedInTheEditorLanguage()
    {
        LayoutEditService editor = new();
        MutableAstObjectNode brush = new() { TypeName = "SolidColorBrush", XmlNamespace = "https://github.com/avaloniaui" };
        MutableAstObjectNode root = new() { TypeName = "UserControl", XmlNamespace = brush.XmlNamespace };
        CultureInfo? previous = UiStrings.Culture;
        try
        {
            UiStrings.Culture = CultureInfo.GetCultureInfo("en");
            Assert.False(editor.TryInsert(brush, new MutableAstObjectNode { TypeName = "Button" }, out string? english));
            Assert.Equal("SolidColorBrush is not a supported visual container.", english);
            Assert.False(editor.TryRemove(root, out english));
            Assert.Equal("The document root cannot be deleted.", english);

            UiStrings.Culture = CultureInfo.GetCultureInfo("de");
            Assert.False(editor.TryInsert(brush, new MutableAstObjectNode { TypeName = "Button" }, out string? german));
            Assert.Equal("SolidColorBrush ist kein unterstützter visueller Container.", german);
            Assert.False(editor.TryRemove(root, out german));
            Assert.Equal("Das Wurzelelement des Dokuments lässt sich nicht löschen.", german);
        }
        finally
        {
            UiStrings.Culture = previous;
        }
    }

    private static Dictionary<string, string> ReadStrings(CultureInfo culture)
    {
        ResourceSet? resources = UiStrings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(resources);

        Dictionary<string, string> strings = new(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in resources)
        {
            if (entry.Value is string value)
            {
                strings[(string)entry.Key] = value;
            }
        }

        return strings;
    }
}
