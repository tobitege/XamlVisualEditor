using System;
using System.Collections.Generic;

namespace XamlVisualEditor.Extensions;

/// <summary>The colour class of an explorer decoration; the host maps each kind to a theme brush.</summary>
public enum ExplorerDecorationKind
{
    Modified,
    Added,
    Deleted,
    Conflicted
}

/// <summary>
/// Marks a file or folder in the File Explorer, the Solution Explorer and the extension trees whose items implement
/// <see cref="IExtensionTreeItemPathProvider"/>: the label takes the colour of <paramref name="Kind"/>, followed by
/// the badge.
/// </summary>
/// <param name="Kind">The colour class.</param>
/// <param name="Badge">A short text after the label, one or two characters; null shows only the colour.</param>
/// <param name="ToolTip">The tooltip of the badge, localized by the extension.</param>
public sealed record ExplorerDecoration(ExplorerDecorationKind Kind, string? Badge = null, string? ToolTip = null);

/// <summary>Decorates files and folders in the explorers.</summary>
public interface IExplorerDecorations
{
    /// <summary>
    /// Creates an empty layer. A node shows the decoration of the first layer, in creation order, that has one for
    /// its path; a node without an entry stays undecorated, also when its folder has one.
    /// </summary>
    IExplorerDecorationLayer CreateLayer();
}

/// <summary>The decorations of one source; disposing the layer removes them.</summary>
public interface IExplorerDecorationLayer : IDisposable
{
    /// <summary>
    /// Replaces all decorations of the layer. Keys are full paths of files and folders; a trailing separator is
    /// ignored. Callable from any thread; the host copies the entries and never calls back into the extension.
    /// </summary>
    void Set(IEnumerable<KeyValuePair<string, ExplorerDecoration>> decorations);
}
