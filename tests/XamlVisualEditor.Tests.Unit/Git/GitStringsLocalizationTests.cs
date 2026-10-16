using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Text.RegularExpressions;
using XamlVisualEditor.GitExtension.Localization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitStringsLocalizationTests
{
    private static readonly Regex PlaceholderPattern = new(@"\{(\d+)", RegexOptions.CultureInvariant);

    [Fact]
    public void GermanResources_TranslateEveryNeutralString()
    {
        Dictionary<string, string> neutral = ReadStrings(CultureInfo.InvariantCulture);
        Dictionary<string, string> german = ReadStrings(CultureInfo.GetCultureInfo("de"));

        Assert.Empty(neutral.Keys.Except(german.Keys).OrderBy(key => key));
        Assert.Empty(german.Keys.Except(neutral.Keys).OrderBy(key => key));
    }

    [Fact]
    public void GermanResources_UseTheNeutralPlaceholders()
    {
        Dictionary<string, string> neutral = ReadStrings(CultureInfo.InvariantCulture);
        Dictionary<string, string> german = ReadStrings(CultureInfo.GetCultureInfo("de"));

        List<string> mismatches = neutral
            .Where(entry => german.TryGetValue(entry.Key, out string? translation)
                && !Placeholders(entry.Value).SetEquals(Placeholders(translation)))
            .Select(entry => entry.Key)
            .OrderBy(key => key)
            .ToList();

        Assert.Empty(mismatches);
    }

    private static Dictionary<string, string> ReadStrings(CultureInfo culture)
    {
        ResourceSet? resources = GitStrings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(resources);

        Dictionary<string, string> strings = new();
        foreach (DictionaryEntry entry in resources)
        {
            if (entry.Value is string value)
            {
                strings[(string)entry.Key] = value;
            }
        }

        return strings;
    }

    private static HashSet<int> Placeholders(string text)
    {
        return PlaceholderPattern.Matches(text)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToHashSet();
    }
}
