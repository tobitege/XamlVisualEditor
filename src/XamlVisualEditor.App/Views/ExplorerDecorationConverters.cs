using Avalonia.Data.Converters;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.App.Views;

/// <summary>Reads the parts of an explorer decoration that a row shows; a node without decoration shows none.</summary>
public static class ExplorerDecorationConverters
{
    /// <summary>Returns whether the decoration has the kind that the converter parameter names.</summary>
    public static readonly IValueConverter IsKind =
        new FuncValueConverter<ExplorerDecoration?, string?, bool>((decoration, kind) =>
            decoration is not null && decoration.Kind.ToString() == kind);

    /// <summary>Returns the badge text.</summary>
    public static readonly IValueConverter Badge =
        new FuncValueConverter<ExplorerDecoration?, string?>(decoration => decoration?.Badge);

    /// <summary>Returns the tooltip of the badge.</summary>
    public static readonly IValueConverter ToolTip =
        new FuncValueConverter<ExplorerDecoration?, string?>(decoration => decoration?.ToolTip);
}
