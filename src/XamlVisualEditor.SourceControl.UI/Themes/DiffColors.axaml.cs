using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace XamlVisualEditor.SourceControl.UI.Themes;

/// <summary>
/// The default line colors of the diff editors for the light and the dark theme, as theme resources under the keys of
/// this class.
/// </summary>
/// <remarks>
/// <see cref="Controls.UnifiedDiffEditor"/> and the diff of the Git Changes panel look a key up from the editor
/// upwards, through its parents and the application resources and styles, with the editor's theme variant; only a key
/// that no resource defines takes the color of this dictionary. An application or theme therefore changes a color by
/// defining a brush under the key, for example in <c>Application.Resources</c> or in a theme dictionary of its own;
/// the color properties of an editor win over both. The editors redraw when their theme variant or a resource that
/// they can reach changes.
/// </remarks>
public sealed partial class DiffColors : ResourceDictionary
{
    /// <summary>The key of the foreground of added lines.</summary>
    public const string ADDED_FOREGROUND_KEY = "DiffAddedForegroundBrush";

    /// <summary>The key of the background of added lines.</summary>
    public const string ADDED_BACKGROUND_KEY = "DiffAddedBackgroundBrush";

    /// <summary>The key of the foreground of removed lines.</summary>
    public const string REMOVED_FOREGROUND_KEY = "DiffRemovedForegroundBrush";

    /// <summary>The key of the background of removed lines.</summary>
    public const string REMOVED_BACKGROUND_KEY = "DiffRemovedBackgroundBrush";

    /// <summary>The key of the foreground of hunk headers.</summary>
    public const string HUNK_HEADER_FOREGROUND_KEY = "DiffHunkHeaderForegroundBrush";

    /// <summary>The key of the background of hunk headers.</summary>
    public const string HUNK_HEADER_BACKGROUND_KEY = "DiffHunkHeaderBackgroundBrush";

    /// <summary>The key of the foreground of file headers.</summary>
    public const string FILE_HEADER_FOREGROUND_KEY = "DiffFileHeaderForegroundBrush";

    /// <summary>The key of the foreground of the marker of a missing newline at the end of a file.</summary>
    public const string NO_NEWLINE_FOREGROUND_KEY = "DiffNoNewlineForegroundBrush";

    private static DiffColors? _defaults;

    /// <summary>Creates the dictionary with its light and dark colors.</summary>
    public DiffColors()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Gets the one dictionary that all editors fall back to.</summary>
    private static DiffColors Defaults => _defaults ??= new DiffColors();

    /// <summary>
    /// Returns the brush of <paramref name="key"/> for <paramref name="control"/>: the resource found from the control
    /// upwards for its theme variant, or else the default of this dictionary.
    /// </summary>
    public static IBrush? Find(Control control, string key)
    {
        if (control.TryFindResource(key, control.ActualThemeVariant, out object? resource))
        {
            return resource as IBrush;
        }

        return Defaults.TryGetResource(key, control.ActualThemeVariant, out resource) ? resource as IBrush : null;
    }
}
