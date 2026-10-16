using XamlVisualEditor.TextEditingExtension.Localization;

namespace XamlVisualEditor.TextEditingExtension.ViewModels;

/// <summary>A color offered for an editor color option; <see cref="Value"/> <see langword="null"/> keeps the theme color.</summary>
public sealed record EditorColorChoice(string? Value, string Label)
{
    /// <summary>Gets the offered colors, with an unknown current color appended so that it stays selectable.</summary>
    public static IReadOnlyList<EditorColorChoice> CreateChoices(string? current)
    {
        List<EditorColorChoice> choices =
        [
            new(null, TextEditingStrings.ThemeColor),
            new("#FFFFFF", TextEditingStrings.ColorWhite),
            new("#000000", TextEditingStrings.ColorBlack),
            new("#1E1E1E", TextEditingStrings.ColorDark),
            new("#D3D3D3", TextEditingStrings.ColorLightGray),
            new("#C0C0C0", TextEditingStrings.ColorSilver),
            new("#808080", TextEditingStrings.ColorGray),
            new("#708090", TextEditingStrings.ColorSlateGray),
            new("#F0F8FF", TextEditingStrings.ColorAliceBlue),
            new("#F5F5DC", TextEditingStrings.ColorBeige),
            new("#FFF8DC", TextEditingStrings.ColorCornsilk),
            new("#FFFFE0", TextEditingStrings.ColorLightYellow),
            new("#B0C4DE", TextEditingStrings.ColorLightSteelBlue)
        ];
        if (!string.IsNullOrWhiteSpace(current) && Find(choices, current) is null)
        {
            choices.Add(new EditorColorChoice(current.Trim(), current.Trim()));
        }

        return choices;
    }

    /// <summary>Finds the choice of a color value; <see langword="null"/> finds the theme color choice.</summary>
    public static EditorColorChoice? Find(IReadOnlyList<EditorColorChoice> choices, string? value)
    {
        return choices.FirstOrDefault(choice => string.Equals(choice.Value, string.IsNullOrWhiteSpace(value) ? null : value.Trim(),
            StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public override string ToString() => Label;
}
