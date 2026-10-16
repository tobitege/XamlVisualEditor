namespace XamlVisualEditor.Extensions;

/// <summary>Describes command metadata used by host UI surfaces.</summary>
public sealed record CommandMetadata(
    string Title,
    string? Category = null,
    string? Icon = null,
    string? When = null,
    string? Keybinding = null,
    string? MacKeybinding = null,
    int Priority = 0)
{
    /// <summary>Gets where the keybinding applies; menus show the gesture wherever it applies.</summary>
    public CommandKeybindingScope KeybindingScope { get; init; }
}

/// <summary>Where a command keybinding applies.</summary>
public enum CommandKeybindingScope
{
    /// <summary>Anywhere in the main window, unless the focused control handles the keys itself.</summary>
    Window,

    /// <summary>Only while a text or code editor has the keyboard focus.</summary>
    TextEditor
}

/// <summary>Stores extension command metadata for host composition.</summary>
public interface ICommandMetadataRegistry
{
    /// <summary>Raised when metadata changes.</summary>
    event EventHandler? Changed;

    /// <summary>Registers metadata for a command.</summary>
    /// <remarks>
    /// A command has one metadata entry: a later registration for the same command id replaces the earlier one.
    /// </remarks>
    /// <returns>
    /// A handle that removes this registration when disposed. Disposing a replaced registration, or disposing
    /// again, has no effect.
    /// </returns>
    IDisposable Register(string commandId, CommandMetadata metadata);

    /// <summary>Gets metadata for a command, if available.</summary>
    bool TryGet(string commandId, out CommandMetadata metadata);

    /// <summary>Gets all registered metadata.</summary>
    IReadOnlyDictionary<string, CommandMetadata> GetAll();
}
