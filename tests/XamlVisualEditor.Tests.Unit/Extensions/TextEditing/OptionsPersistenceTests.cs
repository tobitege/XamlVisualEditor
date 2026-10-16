using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.TextEditingExtension.Services;
using XamlVisualEditor.TextEditingExtension.ViewModels;
using Xunit;
using OptionsPersistence = XamlVisualEditor.TextEditingExtension.TextEditingExtension.OptionsPersistence;
using TextEditingExtensionEntry = XamlVisualEditor.TextEditingExtension.TextEditingExtension;

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
    public void DisposeWaitsForTheRunningSave()
    {
        FakeViewHost view = new();
        CountingStorage storage = new() { WriteDelay = TimeSpan.FromMilliseconds(100) };
        OptionsPersistence persistence = new(view, new TextEditorOptionsStore(storage), null, new NullLogger());

        view.SetOptions(view.Options with { WordWrap = true });
        persistence.Dispose();

        Assert.Equal(1, storage.Writes);
    }

    [AvaloniaFact]
    public async Task OptionsDialogSavesItsResultOnceAfterThePreview()
    {
        FakeViewHost view = new();
        CountingStorage storage = new();
        ScriptedDialogHost dialogs = new();
        ExtensionContext context = CreateContext(view, dialogs, storage);
        await new TextEditingExtensionEntry().ActivateAsync(context, CancellationToken.None);
        TextEditorViewOptions original = view.Options;
        dialogs.Script = async dialog =>
        {
            view.SetOptions(original with { FontSize = 20 });
            dialog.FontSize = 22;
            using IDisposable handler = dialog.CloseInteraction.RegisterHandler(close => close.SetOutput(ReactiveUI.Primitives.RxVoid.Default));
            await dialog.SaveCommand.Execute();
            return true;
        };

        await context.Commands.ExecuteAsync(TextEditingExtensionEntry.OPTIONS_COMMAND_ID, null, CancellationToken.None);
        await context.Subscriptions.OfType<OptionsPersistence>().Single().Saving;

        Assert.Equal(22d, view.Options.FontSize);
        Assert.Equal(1, storage.Writes);
        Assert.Equal(view.Options, await new TextEditorOptionsStore(storage).LoadAsync(CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task OptionsDialogClosedWithoutSavingRestoresTheOriginalAndNeverSavesThePreview()
    {
        FakeViewHost view = new();
        CountingStorage storage = new();
        ScriptedDialogHost dialogs = new();
        ExtensionContext context = CreateContext(view, dialogs, storage);
        await new TextEditingExtensionEntry().ActivateAsync(context, CancellationToken.None);
        TextEditorViewOptions original = view.Options;

        // The window button closes the dialog without a result.
        dialogs.Script = _ =>
        {
            view.SetOptions(original with { FontSize = 20 });
            return Task.FromResult<bool?>(null);
        };

        await context.Commands.ExecuteAsync(TextEditingExtensionEntry.OPTIONS_COMMAND_ID, null, CancellationToken.None);
        await context.Subscriptions.OfType<OptionsPersistence>().Single().Saving;

        Assert.Equal(original, view.Options);
        Assert.Equal(0, storage.Writes);
        Assert.Null(await new TextEditorOptionsStore(storage).LoadAsync(CancellationToken.None));
    }

    private static ExtensionContext CreateContext(ITextEditorViewHost view, IDialogHost dialogHost, IExtensionStorage storage)
    {
        return new ExtensionContext(
            "test.text-editing",
            "/tmp",
            new CommandRegistry(),
            new CommandMetadataRegistry(),
            new ExtensionContributionRegistry(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            dialogHost,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new NoDocumentEditor(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            storage,
            new NullLogger(),
            new List<IDisposable>())
        {
            TextEditorView = view
        };
    }

    // Plays the part of the user while the options dialog is open and returns the dialog result.
    private sealed class ScriptedDialogHost : IDialogHost
    {
        public Func<TextEditorOptionsViewModel, Task<bool?>> Script { get; set; } = _ => Task.FromResult<bool?>(null);

        public IDisposable RegisterDialog(string dialogId, Func<object?, object> factory) => System.Reactive.Disposables.Disposable.Empty;

        public async Task<T?> ShowDialogAsync<T>(string dialogId, object? viewModel, CancellationToken cancellationToken)
        {
            bool? result = await Script((TextEditorOptionsViewModel)viewModel!);
            return (T?)(object?)result;
        }
    }

    private sealed class NoDocumentEditor : IEditorServices
    {
        public IEditorDocument? ActiveDocument => null;

        public event EventHandler<EditorActiveDocumentChangedEventArgs>? ActiveDocumentChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<IEditorDocument> GetOpenDocuments() => [];

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, CancellationToken ct) => Task.FromResult<IEditorDocument?>(null);

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, EditorDocumentOpenBehavior behavior, CancellationToken ct) =>
            Task.FromResult<IEditorDocument?>(null);

        public Task<bool> OpenLocationAsync(LanguageLocation location, CancellationToken ct) => Task.FromResult(false);
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

        public TimeSpan WriteDelay { get; init; }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) => _inner.GetAsync<T>(key, cancellationToken);

        public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("disk full");
            }

            await Task.Delay(WriteDelay, cancellationToken).ConfigureAwait(false);
            await _inner.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
            Writes++;
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
