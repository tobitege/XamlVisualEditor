using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.TextEditingExtension.Services;
using Xunit;
using OptionsPersistence = XamlVisualEditor.TextEditingExtension.TextEditingExtension.OptionsPersistence;

namespace XamlVisualEditor.Tests.Unit.Extensions.TextEditing;

public sealed class OptionsPersistenceTests
{
    [Fact]
    public async Task ChangesAreSavedAndPublishedAsContextKeys()
    {
        FakeViewHost view = new();
        InMemoryExtensionStorage storage = new();
        ContextKeyRegistry keys = new();
        int keyChanges = 0;
        keys.Changed += (_, _) => keyChanges++;
        using OptionsPersistence persistence = new(view, new TextEditorOptionsStore(storage), keys, new NullLogger());
        keyChanges = 0;

        view.SetOptions(view.Options with { ShowSpaces = true, ShowTabs = true, ShowEndOfLine = true });
        await persistence.Saving;

        Assert.Equal(view.Options, await new TextEditorOptionsStore(storage).LoadAsync(CancellationToken.None));
        Assert.True(keys.TryGet("textEditing.allCharacters", out bool allCharacters) && allCharacters);
        Assert.True(keys.TryGet("textEditing.whitespace", out bool whitespace) && whitespace);
        Assert.Equal(1, keyChanges);
    }

    [Fact]
    public async Task PausedPreviewIsSavedOnlyAsTheFinalOptions()
    {
        FakeViewHost view = new();
        CountingStorage storage = new();
        using OptionsPersistence persistence = new(view, new TextEditorOptionsStore(storage), null, new NullLogger());
        TextEditorViewOptions original = view.Options;

        using (persistence.Pause())
        {
            view.SetOptions(original with { FontSize = 20 });
            view.SetOptions(original with { FontSize = 22 });
            view.SetOptions(original);
        }

        await persistence.Saving;
        Assert.Equal(0, storage.Writes);

        using (persistence.Pause())
        {
            view.SetOptions(original with { FontSize = 24 });
        }

        await persistence.Saving;
        Assert.Equal(1, storage.Writes);
    }

    [Fact]
    public async Task FailedSaveIsRetriedWithTheSameOptions()
    {
        FakeViewHost view = new();
        CountingStorage storage = new() { FailNext = true };
        using OptionsPersistence persistence = new(view, new TextEditorOptionsStore(storage), null, new NullLogger());
        TextEditorViewOptions wrapped = view.Options with { WordWrap = true };

        view.SetOptions(wrapped);
        await persistence.Saving;
        Assert.Equal(0, storage.Writes);

        // Closing a dialog without changes saves the current options again.
        persistence.Pause().Dispose();
        await persistence.Saving;

        Assert.Equal(1, storage.Writes);
        Assert.Equal(wrapped, await new TextEditorOptionsStore(storage).LoadAsync(CancellationToken.None));
    }

    [Fact]
    public void SeveralContextKeysChangeWithOneNotification()
    {
        ContextKeyRegistry keys = new();
        int changes = 0;
        keys.Changed += (_, _) => changes++;

        keys.Set([new("a", true), new("b", true)]);
        keys.Set([new("a", true), new("b", true)]);
        keys.Set("A", false);

        Assert.Equal(2, changes);
        Assert.True(keys.TryGet(" a ", out bool a) && !a);
        Assert.False(keys.TryGet("missing", out _));
    }

    private sealed class FakeViewHost : ITextEditorViewHost
    {
        public TextEditorViewOptions Options { get; private set; } = new();

        public bool HasActiveEditor => false;

        public event EventHandler? OptionsChanged;

        public void SetOptions(TextEditorViewOptions options)
        {
            Options = options.Normalize();
            OptionsChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<bool> ExecuteAsync(TextEditorViewAction action, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class CountingStorage : IExtensionStorage
    {
        private readonly InMemoryExtensionStorage _inner = new();

        public int Writes { get; private set; }

        public bool FailNext { get; set; }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) => _inner.GetAsync<T>(key, cancellationToken);

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("disk full");
            }

            Writes++;
            return _inner.SetAsync(key, value, cancellationToken);
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken) => _inner.RemoveAsync(key, cancellationToken);
    }

    private sealed class NullLogger : IExtensionLogger
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? exception = null)
        {
        }
    }
}
