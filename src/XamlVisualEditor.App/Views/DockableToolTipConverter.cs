using System.Globalization;
using Avalonia.Data.Converters;
using XamlVisualEditor.Shell;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Builds the tooltip of a document tab: the full path of a file document, otherwise the title. The first value is
/// the dockable, the second its title; the title binding re-evaluates the tooltip when "Save As" changes the file.
/// </summary>
public static class DockableToolTipConverter
{
    public static readonly IMultiValueConverter Instance = new ToolTipConverter();

    internal static string? GetToolTip(object? dockable, string? title) =>
        dockable is IFileDockable { FilePath: { Length: > 0 } path } ? path : title;

    private sealed class ToolTipConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            GetToolTip(values.Count > 0 ? values[0] : null, values.Count > 1 ? values[1] as string : null);
    }
}
