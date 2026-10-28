using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

internal static class TfvcTestData
{
    public const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";

    public static TfvcWorkspace CreateWorkspace(string name = "DEV01", string localRoot = @"C:\src\Product")
    {
        return new TfvcWorkspace(
            name,
            "Test User",
            "DEV01",
            [
                new TfvcWorkingFolder("$/Product", localRoot, false),
                new TfvcWorkingFolder("$/Product/Secret", string.Empty, true)
            ]);
    }

    public static TfvcPendingChange CreateChange(string serverPath, string? localPath, params string[] changeTypes)
    {
        return new TfvcPendingChange(serverPath, localPath, changeTypes.Length == 0 ? ["Edit"] : changeTypes, false, 12, null, null);
    }
}

internal sealed class FakeTfCommandRunner : ITfCommandRunner
{
    private readonly Queue<TfCommandResult> _results = new();

    public string? ConfiguredTfExePath { get; set; }

    public List<(string WorkingDirectory, IReadOnlyList<string> Arguments)> Calls { get; } = new();

    /// <summary>Runs while a command is running, for example to read a file that exists only during the call.</summary>
    public Action<IReadOnlyList<string>>? OnRun { get; set; }

    public void Enqueue(TfCommandResult result) => _results.Enqueue(result);

    public Task<TfCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add((workingDirectory, arguments));
        OnRun?.Invoke(arguments);
        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new TfCommandResult(0, string.Empty, string.Empty));
    }
}

internal sealed class FakeTfvcClient : ITfvcClient
{
    public string? ConfiguredTfExePath { get; set; }

    public IReadOnlyList<TfvcWorkspace> Workspaces { get; set; } = Array.Empty<TfvcWorkspace>();

    public IReadOnlyList<TfvcPendingChange> PendingChanges { get; set; } = Array.Empty<TfvcPendingChange>();

    public Dictionary<string, IReadOnlyList<TfvcPendingChange>> PendingChangesByLocalPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? CheckOutFailure { get; set; }

    public string CheckInOutput { get; set; } = "Changeset #4711 checked in.";

    public List<IReadOnlyList<string>> CheckOuts { get; } = new();

    public List<(string Comment, IReadOnlyList<string> Items)> CheckIns { get; } = new();

    public List<IReadOnlyList<string>> Undos { get; } = new();

    public int PendingChangeQueries { get; private set; }

    public int FileStatusQueries { get; private set; }

    public Exception? WorkspacesFailure { get; set; }

    /// <summary>When set, the workspace query completes with this task instead of <see cref="Workspaces"/>.</summary>
    public TaskCompletionSource<IReadOnlyList<TfvcWorkspace>>? WorkspacesGate { get; set; }

    public Task<IReadOnlyList<TfvcWorkspace>> GetWorkspacesAsync(string collectionUrl, CancellationToken cancellationToken)
    {
        if (WorkspacesGate is { } gate)
        {
            return gate.Task;
        }

        return WorkspacesFailure is null ? Task.FromResult(Workspaces) : Task.FromException<IReadOnlyList<TfvcWorkspace>>(WorkspacesFailure);
    }

    public Task<IReadOnlyList<TfvcPendingChange>> GetPendingChangesAsync(string collectionUrl, TfvcWorkspace workspace, CancellationToken cancellationToken)
    {
        PendingChangeQueries++;
        return Task.FromResult(PendingChanges);
    }

    public Exception? FileStatusFailure { get; set; }

    public Task<IReadOnlyList<TfvcPendingChange>> GetPendingChangesForLocalPathAsync(string localPath, CancellationToken cancellationToken)
    {
        FileStatusQueries++;
        if (FileStatusFailure is not null)
        {
            throw FileStatusFailure;
        }

        return Task.FromResult(PendingChangesByLocalPath.TryGetValue(localPath, out IReadOnlyList<TfvcPendingChange>? changes)
            ? changes
            : Array.Empty<TfvcPendingChange>());
    }

    public List<bool> CheckOutRecursion { get; } = new();

    /// <summary>When set, a check-out waits for this task before it completes.</summary>
    public TaskCompletionSource? CheckOutGate { get; set; }

    /// <summary>Raised when a check-out starts, with the checked out paths.</summary>
    public event Action<IReadOnlyList<string>>? CheckOutStarted;

    public async Task<TfCommandResult> CheckOutAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, bool recursive, CancellationToken cancellationToken)
    {
        CheckOuts.Add(localPaths);
        CheckOutRecursion.Add(recursive);
        CheckOutStarted?.Invoke(localPaths);
        if (CheckOutGate is { } gate)
        {
            await gate.Task;
        }

        if (CheckOutFailure is not null)
        {
            throw CheckOutFailure;
        }

        foreach (string path in localPaths)
        {
            string serverPath = workspace.TryGetServerPath(path) ?? path;
            PendingChangesByLocalPath[path] = [TfvcTestData.CreateChange(serverPath, path)];
        }

        return new TfCommandResult(0, string.Empty, string.Empty);
    }

    public List<IReadOnlyList<string>> Adds { get; } = new();

    public Task<TfCommandResult> AddAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        Adds.Add(localPaths);
        return AddFailure is null
            ? Task.FromResult(new TfCommandResult(0, string.Empty, string.Empty))
            : Task.FromException<TfCommandResult>(AddFailure);
    }

    public Exception? AddFailure { get; set; }

    public List<IReadOnlyList<string>> Deletes { get; } = new();

    public Task<TfCommandResult> DeleteAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        Deletes.Add(localPaths);
        return Task.FromResult(new TfCommandResult(0, string.Empty, string.Empty));
    }

    public List<string> LocalChangeCandidates { get; } = new();

    public List<string> LocalChangeScopes { get; } = new();

    public Task<IReadOnlyCollection<string>> PreviewLocalChangesAsync(string localFolder, CancellationToken cancellationToken)
    {
        LocalChangeScopes.Add(localFolder);
        return Task.FromResult<IReadOnlyCollection<string>>(LocalChangeCandidates.ToArray());
    }

    public Task<TfCommandResult> UndoAsync(TfvcWorkspace workspace, IReadOnlyList<string> itemSpecs, bool recursive, CancellationToken cancellationToken)
    {
        Undos.Add(itemSpecs);
        return Task.FromResult(new TfCommandResult(0, string.Empty, string.Empty));
    }

    /// <summary>Gets or sets whether a check-in call with an item list reports a too long command line.</summary>
    public bool RejectCheckInItemLists { get; set; }

    public Task<TfCommandResult> CheckInAsync(TfvcWorkspace workspace, string comment, IReadOnlyList<string> itemSpecs, CancellationToken cancellationToken)
    {
        CheckIns.Add((comment, itemSpecs));
        return RejectCheckInItemLists && itemSpecs.Count > 0
            ? Task.FromException<TfCommandResult>(new TfCommandLineTooLongException())
            : Task.FromResult(new TfCommandResult(0, CheckInOutput, string.Empty));
    }

    public List<(string Name, string? Comment, IReadOnlyList<string> Items)> Shelves { get; } = new();

    /// <summary>Gets or sets whether a shelve call with an item list reports a too long command line.</summary>
    public bool RejectShelveItemLists { get; set; }

    public Task<TfCommandResult> ShelveAsync(TfvcWorkspace workspace, string shelvesetName, string? comment, IReadOnlyList<string> itemSpecs, CancellationToken cancellationToken)
    {
        Shelves.Add((shelvesetName, comment, itemSpecs));
        return RejectShelveItemLists && itemSpecs.Count > 0
            ? Task.FromException<TfCommandResult>(new TfCommandLineTooLongException())
            : Task.FromResult(new TfCommandResult(0, string.Empty, string.Empty));
    }

    public Task<TfCommandResult> GetLatestAsync(TfvcWorkspace workspace, string itemSpec, bool recursive, CancellationToken cancellationToken)
        => Task.FromResult(new TfCommandResult(0, string.Empty, string.Empty));

    public Exception? DifferenceFailure { get; set; }

    public Task<TfCommandResult> DifferenceAsync(TfvcWorkspace workspace, string localPath, CancellationToken cancellationToken)
        => DifferenceFailure is null
            ? Task.FromResult(new TfCommandResult(1, "--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new\n", string.Empty))
            : Task.FromException<TfCommandResult>(DifferenceFailure);

    public Task<string?> DetectCollectionUrlAsync(string localDirectory, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public List<(int ChangesetId, string Comment)> ChangesetCommentUpdates { get; } = new();

    public Task<TfCommandResult> UpdateChangesetCommentAsync(string collectionUrl, int changesetId, string comment, CancellationToken cancellationToken)
    {
        ChangesetCommentUpdates.Add((changesetId, comment));
        return Task.FromResult(new TfCommandResult(0, string.Empty, string.Empty));
    }
}

internal sealed class FakeTfvcServerClient : ITfvcServerClient
{
    public HashSet<string> ExistingItems { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, IReadOnlyList<TfvcServerItem>> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<int, TfvcWorkItemInfo> WorkItems { get; } = new();

    public List<(int WorkItemId, int ChangesetId)> Links { get; } = new();

    public List<string> ChildrenRequests { get; } = new();

    public TaskCompletionSource? ChildrenGate { get; set; }

    public async Task<IReadOnlyList<TfvcServerItem>> GetChildrenAsync(string collectionUrl, string serverPath, CancellationToken cancellationToken)
    {
        ChildrenRequests.Add(serverPath);
        if (ChildrenGate is { } gate)
        {
            await gate.Task;
        }

        return Children.TryGetValue(serverPath, out IReadOnlyList<TfvcServerItem>? items) ? items : Array.Empty<TfvcServerItem>();
    }

    public List<string> ItemRequests { get; } = new();

    public Task<TfvcServerItem?> GetItemAsync(string collectionUrl, string serverPath, CancellationToken cancellationToken)
    {
        ItemRequests.Add(serverPath);
        bool isFolder = Children.ContainsKey(serverPath);
        return Task.FromResult(ExistingItems.Contains(serverPath) || isFolder ? new TfvcServerItem(serverPath, isFolder, 10, null) : null);
    }

    public List<string> RecursiveItemRequests { get; } = new();

    public Exception? RecursiveItemsFailure { get; set; }

    public Task<IReadOnlyList<TfvcServerItem>> GetItemsRecursiveAsync(string collectionUrl, string serverPath, CancellationToken cancellationToken)
    {
        RecursiveItemRequests.Add(serverPath);
        if (RecursiveItemsFailure is not null)
        {
            return Task.FromException<IReadOnlyList<TfvcServerItem>>(RecursiveItemsFailure);
        }

        List<TfvcServerItem> items = new();
        foreach (string path in ExistingItems)
        {
            if (TfvcWorkspace.IsSameOrChildServerPath(path, TfvcWorkspace.NormalizeServerPath(serverPath)))
            {
                items.Add(new TfvcServerItem(path, Children.ContainsKey(path), 10, null));
            }
        }

        return Task.FromResult<IReadOnlyList<TfvcServerItem>>(items);
    }

    public List<TfvcChangesetInfo> History { get; } = new();

    public Dictionary<int, IReadOnlyList<TfvcChangesetChange>> ChangesetChanges { get; } = new();

    public List<TfvcLabelInfo> LabelList { get; } = new();

    public string? CurrentUserId { get; set; }

    public List<string?> AuthorFilters { get; } = new();

    public Task<IReadOnlyList<TfvcChangesetInfo>> GetChangesetsAsync(string collectionUrl, string serverPath, string? author, int skip, int top, CancellationToken cancellationToken)
    {
        AuthorFilters.Add(author);
        List<TfvcChangesetInfo> matching = History.FindAll(changeset => author is null || string.Equals(changeset.Author, author, StringComparison.OrdinalIgnoreCase));
        List<TfvcChangesetInfo> page = new();
        for (int i = skip; i < matching.Count && page.Count < top; i++)
        {
            page.Add(matching[i]);
        }

        return Task.FromResult<IReadOnlyList<TfvcChangesetInfo>>(page);
    }

    public Task<string?> GetCurrentUserIdAsync(string collectionUrl, CancellationToken cancellationToken)
        => Task.FromResult(CurrentUserId);

    public Task<IReadOnlyList<TfvcChangesetChange>> GetChangesetChangesAsync(string collectionUrl, int changesetId, CancellationToken cancellationToken)
        => Task.FromResult(ChangesetChanges.TryGetValue(changesetId, out IReadOnlyList<TfvcChangesetChange>? changes) ? changes : Array.Empty<TfvcChangesetChange>());

    public Task<IReadOnlyList<TfvcLabelInfo>> GetLabelsAsync(string collectionUrl, string serverPath, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TfvcLabelInfo>>(LabelList);

    public Task<TfvcWorkItemInfo?> GetWorkItemAsync(string collectionUrl, int id, CancellationToken cancellationToken)
        => Task.FromResult(WorkItems.TryGetValue(id, out TfvcWorkItemInfo? info) ? info : null);

    public Task LinkChangesetAsync(string collectionUrl, int workItemId, int changesetId, CancellationToken cancellationToken)
    {
        Links.Add((workItemId, changesetId));
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryExtensionStorage : IExtensionStorage
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        return Task.FromResult(_values.TryGetValue(key, out string? json) ? JsonSerializer.Deserialize<T>(json) : default);
    }

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        _values[key] = JsonSerializer.Serialize(value);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }
}

internal sealed class FakeWorkspaceInfo : IWorkspaceInfo
{
    public string? WorkspacePath { get; set; }

    public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged;

    public void Change(string? path)
    {
        WorkspacePath = path;
        WorkspaceChanged?.Invoke(this, new WorkspaceChangedEventArgs(path));
    }
}

internal sealed class NullExtensionLogger : IExtensionLogger
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

internal sealed class RecordingExtensionLogger : IExtensionLogger
{
    private readonly object _gate = new();
    private readonly List<string> _entries = new();

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Info(string message) => Add(message);

    public void Warn(string message) => Add(message);

    public void Error(string message, Exception? exception = null) => Add(message);

    private void Add(string message)
    {
        lock (_gate)
        {
            _entries.Add(message);
        }
    }
}

internal sealed class RecordingWindow : IWindow
{
    public List<string> Errors { get; } = new();

    public string? InputBoxResult { get; set; }

    public List<string> Infos { get; } = new();

    public Task ShowInformationMessageAsync(string message, CancellationToken cancellationToken)
    {
        Infos.Add(message);
        return Task.CompletedTask;
    }

    public Task ShowWarningMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ShowErrorMessageAsync(string message, CancellationToken cancellationToken)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }

    /// <summary>Gets the answers of the next input boxes, in order; once it is empty, <see cref="InputBoxResult"/> answers.</summary>
    public Queue<string?> InputBoxResults { get; } = new();

    /// <summary>Gets the options of the shown input boxes.</summary>
    public List<InputBoxOptions> InputBoxes { get; } = new();

    public Task<string?> ShowInputBoxAsync(InputBoxOptions options, CancellationToken cancellationToken)
    {
        InputBoxes.Add(options);
        return Task.FromResult(InputBoxResults.Count > 0 ? InputBoxResults.Dequeue() : InputBoxResult);
    }

    /// <summary>Gets or sets what the quick pick returns for its items; null cancels every quick pick.</summary>
    public Func<IReadOnlyList<QuickPickItem>, QuickPickItem?>? QuickPick { get; set; }

    public List<(IReadOnlyList<QuickPickItem> Items, QuickPickOptions Options)> QuickPicks { get; } = new();

    public Task<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions options, CancellationToken cancellationToken)
    {
        QuickPicks.Add((items, options));
        return Task.FromResult(QuickPick?.Invoke(items));
    }

    public IOutputChannel CreateOutputChannel(string name) => throw new NotSupportedException();

    public Task<IReadOnlyList<OutputChannelInfo>> GetOutputChannelsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<OutputChannelInfo>>(Array.Empty<OutputChannelInfo>());

#pragma warning disable CS0067
    public event EventHandler<OutputChannelEventArgs>? OutputChannelCreated;
    public event EventHandler<OutputChannelEventArgs>? OutputChannelRemoved;
    public event EventHandler<OutputChannelMessageEventArgs>? OutputChannelMessage;
    public event EventHandler<OutputChannelClearedEventArgs>? OutputChannelCleared;
#pragma warning restore CS0067

    public IStatusBarItem CreateStatusBarItem(StatusBarAlignment alignment, int priority) => throw new NotSupportedException();
}

/// <summary>Records the view ids passed to the view host.</summary>
internal sealed class RecordingExtensionViewHost : IExtensionViewHost
{
    public List<string> Shown { get; } = new();

    public List<string> Toggled { get; } = new();

    /// <summary>Gets or sets what runs when a view is shown, as a host that attaches the view at that moment.</summary>
    public Action<string>? Showing { get; set; }

#pragma warning disable CS0067
    public event EventHandler<ExtensionViewVisibilityChangedEventArgs>? VisibilityChanged;
    public event EventHandler<ExtensionViewFocusChangedEventArgs>? FocusChanged;
#pragma warning restore CS0067

    public Task ShowAsync(string viewId, CancellationToken cancellationToken)
    {
        Shown.Add(viewId);
        Showing?.Invoke(viewId);
        return Task.CompletedTask;
    }

    public Task ToggleAsync(string viewId, CancellationToken cancellationToken)
    {
        Toggled.Add(viewId);
        return Task.CompletedTask;
    }

    public Task<bool> IsVisibleAsync(string viewId, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task ActivateAsync(string viewId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Gets the shown documents in order, also those that were open already.</summary>
    public List<(string Id, string Title, string? ToolTip)> ShownDocuments { get; } = new();

    /// <summary>Gets the view models of the open documents by id.</summary>
    public Dictionary<string, object> DocumentViewModels { get; } = new();

    public Task<object?> ShowDocumentAsync(
        string documentId,
        string title,
        string? toolTip,
        Func<object> createViewModel,
        CancellationToken cancellationToken)
    {
        ShownDocuments.Add((documentId, title, toolTip));
        if (!DocumentViewModels.TryGetValue(documentId, out object? viewModel))
        {
            viewModel = createViewModel();
            DocumentViewModels.Add(documentId, viewModel);
        }

        return Task.FromResult<object?>(viewModel);
    }
}

internal sealed class ScriptedDialogHost : IDialogHost
{
    private readonly Func<string, object?, object?> _respond;

    public ScriptedDialogHost(Func<string, object?, object?> respond)
    {
        _respond = respond;
    }

    public List<(string DialogId, object? ViewModel)> Requests { get; } = new();

    public IDisposable RegisterDialog(string dialogId, Func<object?, object> factory) => throw new NotSupportedException();

    public Task<T?> ShowDialogAsync<T>(string dialogId, object? viewModel, CancellationToken cancellationToken)
    {
        Requests.Add((dialogId, viewModel));
        return Task.FromResult(_respond(dialogId, viewModel) is T result ? result : default);
    }
}

internal sealed class ConfirmingDialogHost : IDialogHost
{
    public int Confirmations { get; private set; }

    public IDisposable RegisterDialog(string dialogId, Func<object?, object> factory) => throw new NotSupportedException();

    public Task<T?> ShowDialogAsync<T>(string dialogId, object? viewModel, CancellationToken cancellationToken)
    {
        Confirmations++;
        return Task.FromResult((T?)(object)true);
    }
}

internal sealed class FakeEditorDocument : IEditorDocument
{
    private int _textReads;

    public FakeEditorDocument(string filePath, string text)
    {
        FilePath = filePath;
        Text = text;
    }

    public string FilePath { get; }

    public string? LanguageId => null;

    public string Text { get; set; }

    public int CaretOffset { get; set; }

    public int SelectionStart { get; set; }

    public int SelectionLength { get; set; }

    public event EventHandler<EditorDocumentChangedEventArgs>? Changed;

#pragma warning disable CS0067
    public event EventHandler<EditorSelectionChangedEventArgs>? SelectionChanged;
#pragma warning restore CS0067

    /// <summary>Gets how often the text was read.</summary>
    public int TextReads => Volatile.Read(ref _textReads);

    public Task<string> GetTextAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _textReads);
        return Task.FromResult(Text);
    }

    /// <summary>Gets every call of <see cref="ApplyEditsAsync"/>, which counts as one undo step each.</summary>
    public List<IReadOnlyList<TextEdit>> AppliedEdits { get; } = new();

    // Applies the edits from the last to the first, so the offsets of the earlier ones stay valid.
    public Task ApplyEditsAsync(IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        AppliedEdits.Add(edits);
        string text = Text;
        foreach (TextEdit edit in edits.OrderByDescending(edit => edit.Offset))
        {
            text = text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.NewText);
        }

        Edit(text);
        return Task.CompletedTask;
    }

    public void Edit(string text)
    {
        Text = text;
        Changed?.Invoke(this, new EditorDocumentChangedEventArgs { FilePath = FilePath });
    }
}

internal sealed class FakeEditorServices : IEditorServices
{
    private readonly List<IEditorDocument> _documents = new();

    public IEditorDocument? ActiveDocument { get; private set; }

    public List<string> OpenedFiles { get; } = new();

    public Dictionary<string, bool> ManagedWriteProtection { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetWriteProtectionManaged(string filePath, bool managed) => ManagedWriteProtection[filePath] = managed;

    public event EventHandler<EditorActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

    public event EventHandler? OpenDocumentsChanged;

    /// <summary>Opens a document the way a workspace edit does, without making it the active document.</summary>
    public void OpenWithoutActivation(IEditorDocument document)
    {
        _documents.Add(document);
        OpenDocumentsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Activate(IEditorDocument document)
    {
        if (!_documents.Contains(document))
        {
            _documents.Add(document);
        }

        ActiveDocument = document;
        ActiveDocumentChanged?.Invoke(this, new EditorActiveDocumentChangedEventArgs { Document = document });
    }

    /// <summary>Closes a document; closing the active document leaves no active document.</summary>
    public void Close(IEditorDocument document)
    {
        _documents.Remove(document);
        if (ReferenceEquals(ActiveDocument, document))
        {
            ActiveDocument = null;
            ActiveDocumentChanged?.Invoke(this, new EditorActiveDocumentChangedEventArgs { Document = null });
        }
    }

    public IReadOnlyList<IEditorDocument> GetOpenDocuments() => _documents;

    public Task<IEditorDocument?> OpenDocumentAsync(string filePath, CancellationToken ct)
        => OpenDocumentAsync(filePath, EditorDocumentOpenBehavior.AllowWorkspaceLoad, ct);

    // Returns the open document of the file, if any; the fake opens no new documents.
    public Task<IEditorDocument?> OpenDocumentAsync(string filePath, EditorDocumentOpenBehavior behavior, CancellationToken ct)
    {
        OpenedFiles.Add(filePath);
        return Task.FromResult(_documents.FirstOrDefault(document =>
            string.Equals(Path.GetFullPath(document.FilePath), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase)));
    }

    public Task<bool> OpenLocationAsync(LanguageLocation location, CancellationToken ct) => Task.FromResult(false);
}
