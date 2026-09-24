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

    public void Enqueue(TfCommandResult result) => _results.Enqueue(result);

    public Task<TfCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add((workingDirectory, arguments));
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

    public Task<IReadOnlyList<TfvcWorkspace>> GetWorkspacesAsync(string collectionUrl, CancellationToken cancellationToken)
        => Task.FromResult(Workspaces);

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

    public Task<TfCommandResult> CheckOutAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, bool recursive, CancellationToken cancellationToken)
    {
        CheckOuts.Add(localPaths);
        CheckOutRecursion.Add(recursive);
        if (CheckOutFailure is not null)
        {
            throw CheckOutFailure;
        }

        foreach (string path in localPaths)
        {
            string serverPath = workspace.TryGetServerPath(path) ?? path;
            PendingChangesByLocalPath[path] = [TfvcTestData.CreateChange(serverPath, path)];
        }

        return Task.FromResult(new TfCommandResult(0, string.Empty, string.Empty));
    }

    public Task<TfCommandResult> AddAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
        => Task.FromResult(new TfCommandResult(0, string.Empty, string.Empty));

    public Task<TfCommandResult> UndoAsync(TfvcWorkspace workspace, IReadOnlyList<string> itemSpecs, bool recursive, CancellationToken cancellationToken)
    {
        Undos.Add(itemSpecs);
        return Task.FromResult(new TfCommandResult(0, string.Empty, string.Empty));
    }

    public Task<TfCommandResult> CheckInAsync(TfvcWorkspace workspace, string comment, IReadOnlyList<string> itemSpecs, CancellationToken cancellationToken)
    {
        CheckIns.Add((comment, itemSpecs));
        return Task.FromResult(new TfCommandResult(0, CheckInOutput, string.Empty));
    }

    public Task<TfCommandResult> ShelveAsync(TfvcWorkspace workspace, string shelvesetName, string? comment, IReadOnlyList<string> itemSpecs, CancellationToken cancellationToken)
        => Task.FromResult(new TfCommandResult(0, string.Empty, string.Empty));

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

internal sealed class RecordingWindow : IWindow
{
    public List<string> Errors { get; } = new();

    public string? InputBoxResult { get; set; }

    public Task ShowInformationMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ShowWarningMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ShowErrorMessageAsync(string message, CancellationToken cancellationToken)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }

    public Task<string?> ShowInputBoxAsync(InputBoxOptions options, CancellationToken cancellationToken) => Task.FromResult(InputBoxResult);

    public Task<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions options, CancellationToken cancellationToken)
        => Task.FromResult<QuickPickItem?>(null);

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

    public Task<string> GetTextAsync(CancellationToken ct) => Task.FromResult(Text);

    public Task ApplyEditsAsync(IReadOnlyList<TextEdit> edits, CancellationToken ct) => Task.CompletedTask;

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

    public event EventHandler<EditorActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

    public void Activate(IEditorDocument document)
    {
        if (!_documents.Contains(document))
        {
            _documents.Add(document);
        }

        ActiveDocument = document;
        ActiveDocumentChanged?.Invoke(this, new EditorActiveDocumentChangedEventArgs { Document = document });
    }

    public IReadOnlyList<IEditorDocument> GetOpenDocuments() => _documents;

    public Task<IEditorDocument?> OpenDocumentAsync(string filePath, CancellationToken ct)
        => OpenDocumentAsync(filePath, EditorDocumentOpenBehavior.AllowWorkspaceLoad, ct);

    public Task<IEditorDocument?> OpenDocumentAsync(string filePath, EditorDocumentOpenBehavior behavior, CancellationToken ct)
    {
        OpenedFiles.Add(filePath);
        return Task.FromResult<IEditorDocument?>(null);
    }

    public Task<bool> OpenLocationAsync(LanguageLocation location, CancellationToken ct) => Task.FromResult(false);
}
