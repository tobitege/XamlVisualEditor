using System.Text.Json;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.TextEditingExtension.Services;

/// <summary>Persists the text editor view options in the extension storage.</summary>
public sealed class TextEditorOptionsStore
{
    private const string OPTIONS_KEY = "textEditing.viewOptions";

    private readonly IExtensionStorage _storage;

    /// <summary>Creates the store.</summary>
    public TextEditorOptionsStore(IExtensionStorage storage)
    {
        _storage = storage;
    }

    /// <summary>Loads the saved options; unreadable or missing options yield <see langword="null"/>.</summary>
    public async Task<TextEditorViewOptions?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            TextEditorViewOptions? options = await _storage.GetAsync<TextEditorViewOptions>(OPTIONS_KEY, cancellationToken)
                .ConfigureAwait(false);
            return options?.Normalize();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves the options.</summary>
    public Task SaveAsync(TextEditorViewOptions options, CancellationToken cancellationToken)
    {
        return _storage.SetAsync(OPTIONS_KEY, options.Normalize(), cancellationToken);
    }
}
