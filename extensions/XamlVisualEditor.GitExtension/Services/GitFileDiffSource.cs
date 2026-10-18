using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>The line diff of a file against its version in the index or in HEAD.</summary>
/// <param name="OldLines">The lines of that version; null when there is none to compare with.</param>
/// <param name="NewLines">The lines of the open document, or of the saved file when it is not open.</param>
/// <param name="Blocks">The change blocks; empty without <paramref name="OldLines"/>.</param>
/// <param name="IsChanged">
/// Whether git reports the saved file as changed against the version. Without blocks, only bytes that the line
/// comparison ignores differ then: line endings, the byte order mark or the final newline.
/// </param>
internal sealed record GitFileDiff(
    IReadOnlyList<string>? OldLines,
    IReadOnlyList<string> NewLines,
    IReadOnlyList<LineDiffBlock> Blocks,
    bool IsChanged);

/// <summary>
/// Compares a file with its version in the index or in HEAD whenever that version or the text of its open document
/// changes. Changes within <see cref="Delay"/> of each other lead to one comparison at the end, which reads the text
/// once and cancels a comparison that still runs. The version is read again only when its object id changes; a file
/// without such a version, a binary file, a Git LFS pointer and a version larger than <see cref="MAX_BLOB_BYTES"/>
/// have nothing to compare with.
/// </summary>
internal static class GitFileDiffSource
{
    /// <summary>The largest version that is compared.</summary>
    public const int MAX_BLOB_BYTES = 16 * 1024 * 1024;

    /// <summary>Above this many inserted and deleted lines the changed middle of a file becomes one block.</summary>
    public const int MAX_EDIT_DISTANCE = 2000;

    // Git takes a file with a NUL in its first 8000 bytes for binary.
    private const int BINARY_PROBE_LENGTH = 8000;

    private static readonly byte[] LfsPointerStart = "version https://git-lfs.github.com/spec/v1"u8.ToArray();

    /// <summary>Gets the quiet time after the last change before the file is compared.</summary>
    public static TimeSpan Delay { get; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Returns the diffs of the file at <paramref name="fullPath"/>; the first follows the first quiet time after the
    /// subscription. A diff arrives on a thread-pool thread, or on the thread that advances <paramref name="scheduler"/>.
    /// </summary>
    /// <param name="session">Provides the snapshots and the content service.</param>
    /// <param name="editor">Provides the open document of the file; null reads the saved file.</param>
    /// <param name="fullPath">The full path of the file.</param>
    /// <param name="revision">The version to compare with.</param>
    /// <param name="scheduler">Times <see cref="Delay"/>.</param>
    /// <param name="refreshes">Further signals to compare again, such as a refresh button.</param>
    /// <param name="logger">Receives failures to read the file.</param>
    public static IObservable<GitFileDiff> Observe(
        GitSession session,
        IEditorServices? editor,
        string fullPath,
        GitFileRevision revision,
        IScheduler scheduler,
        IObservable<Unit>? refreshes = null,
        IExtensionLogger? logger = null)
    {
        return Observable.Defer(() =>
        {
            Source source = new(session, fullPath, revision, logger);
            IObservable<Unit> baseChanges = session.Snapshots
                .Select(source.BaseKey)
                .DistinctUntilChanged()
                .Select(_ => Unit.Default);
            IObservable<Unit> textChanges = Observable.Switch(DocumentOf(editor, fullPath)
                .Select(document =>
                {
                    source.Follow(document);
                    return document is null
                        ? Observable.Return(Unit.Default)
                        : Observable.FromEventPattern<EditorDocumentChangedEventArgs>(
                                handler => document.Changed += handler,
                                handler => document.Changed -= handler)
                            .Select(_ => Unit.Default)
                            .StartWith(Unit.Default);
                }));
            return Observable.Switch(baseChanges
                .Merge(textChanges)
                .Merge(refreshes ?? Observable.Never<Unit>())
                .Throttle(Delay, scheduler)
                .Select(_ => Observable.FromAsync(source.ComputeAsync)));
        });
    }

    /// <summary>Returns the lines of a version, or null for no version, a binary file, a Git LFS pointer or a cut version.</summary>
    internal static string[]? ToLines(GitFileContent? content)
    {
        if (content is not { IsTruncated: false, Bytes: { } bytes }
            || bytes.AsSpan(0, Math.Min(bytes.Length, BINARY_PROBE_LENGTH)).Contains((byte)0)
            || bytes.AsSpan().StartsWith(LfsPointerStart))
        {
            return null;
        }

        return LineDiff.SplitLines(SharedFileReader.Decode(bytes));
    }

    // The open document of the file, looked up again whenever the active document changes, so a document opened later
    // replaces the saved file.
    private static IObservable<IEditorDocument?> DocumentOf(IEditorServices? editor, string fullPath)
    {
        if (editor is null)
        {
            return Observable.Return<IEditorDocument?>(null);
        }

        return Observable.FromEventPattern<EditorActiveDocumentChangedEventArgs>(
                handler => editor.ActiveDocumentChanged += handler,
                handler => editor.ActiveDocumentChanged -= handler)
            .Select(_ => Unit.Default)
            .StartWith(Unit.Default)
            .Select(_ => editor.GetOpenDocuments().FirstOrDefault(document =>
                FileSystemPathComparison.Equals(Path.GetFullPath(document.FilePath), fullPath)))
            .DistinctUntilChanged();
    }

    private sealed class Source(GitSession session, string fullPath, GitFileRevision revision, IExtensionLogger? logger)
    {
        private volatile IEditorDocument? _document;
        private volatile BaseVersion? _base;

        /// <summary>Reads the text from <paramref name="document"/> from now on, or from the saved file for null.</summary>
        public void Follow(IEditorDocument? document) => _document = document;

        /// <summary>
        /// Names the version: the repository and the object id of the file in the index, or the HEAD commit, which also
        /// stands for the index entry of a file without changes. Null outside a repository.
        /// </summary>
        public string? BaseKey(GitSnapshot snapshot)
        {
            if (snapshot.State != GitSessionState.Repository || snapshot.RepositoryRoot is not { } root
                || GitCommandTarget.ToRepositoryPath(root, fullPath) is not { Length: > 0 } path)
            {
                return null;
            }

            string? objectId = revision == GitFileRevision.Index
                ? snapshot.Changes.FirstOrDefault(change => FileSystemPathComparison.Equals(change.Path, path))?.IndexObjectId
                : null;
            return root + "\n" + (objectId ?? snapshot.Status?.HeadSha);
        }

        public async Task<GitFileDiff> ComputeAsync(CancellationToken cancellationToken)
        {
            try
            {
                GitSnapshot snapshot = session.Current;
                string text = await ReadTextAsync(cancellationToken).ConfigureAwait(false);
                string[]? oldLines = await ReadBaseAsync(snapshot, cancellationToken).ConfigureAwait(false);
                bool isChanged = IsChanged(snapshot);
                return await Task.Run(
                    () =>
                    {
                        string[] newLines = LineDiff.SplitLines(text);
                        IReadOnlyList<LineDiffBlock> blocks = oldLines is null
                            ? []
                            : LineDiff.Compute(oldLines, newLines, MAX_EDIT_DISTANCE, cancellationToken);
                        return new GitFileDiff(oldLines, newLines, blocks, isChanged);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger?.Error(GitStrings.LogCompareFailed, exception);
                return new GitFileDiff(null, [], [], false);
            }
        }

        // Whether git reports the saved file as changed against the version: in the working tree for the index, in
        // the index or the working tree for HEAD.
        private bool IsChanged(GitSnapshot snapshot)
        {
            if (snapshot.RepositoryRoot is not { } root
                || GitCommandTarget.ToRepositoryPath(root, fullPath) is not { Length: > 0 } path
                || snapshot.Changes.FirstOrDefault(change => FileSystemPathComparison.Equals(change.Path, path)) is not { } change)
            {
                return false;
            }

            return change.WorkTreeStatus != GitChangeKind.None
                || (revision == GitFileRevision.Head && change.IndexStatus != GitChangeKind.None);
        }

        private async Task<string> ReadTextAsync(CancellationToken cancellationToken)
        {
            if (_document is { } document)
            {
                return await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            }

            return File.Exists(fullPath)
                ? SharedFileReader.Decode(await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false))
                : string.Empty;
        }

        private async Task<string[]?> ReadBaseAsync(GitSnapshot snapshot, CancellationToken cancellationToken)
        {
            string? key = BaseKey(snapshot);
            if (_base is { } known && known.Key == key)
            {
                return known.Lines;
            }

            string[]? lines = null;
            if (key is not null && session.Services.Content is { } content)
            {
                string root = snapshot.RepositoryRoot!;
                string path = GitCommandTarget.ToRepositoryPath(root, fullPath)!;
                lines = ToLines(await content.GetFileContentAsync(root, path, revision, MAX_BLOB_BYTES, cancellationToken)
                    .ConfigureAwait(false));
            }

            _base = new BaseVersion(key, lines);
            return lines;
        }
    }

    private sealed record BaseVersion(string? Key, string[]? Lines);
}
