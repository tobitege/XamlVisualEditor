using System.Globalization;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ReactiveUI;
using ReactiveUI.Primitives.Signals;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.SourceControl.UI.Models;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.GitExtension;

/// <summary>
/// ViewModel of a document that compares a file with its version in HEAD or in the index. It shows one hunk per change
/// block and follows the open document of the file while it is edited; a closed file is read from disk, again on
/// <see cref="RefreshCommand"/>.
/// </summary>
public sealed partial class GitCompareDocumentViewModel : ReactiveObject, IDisposable
{
    /// <summary>The number of unchanged lines around a change.</summary>
    public const int CONTEXT_LINES = 3;

    private readonly CompositeDisposable _disposables = new();
    private readonly Subject<Unit> _refreshes = new();
    private GitFileDiff? _diff;
    private int? _lineToShow;

    /// <summary>Creates the comparison and starts it.</summary>
    /// <param name="session">The Git session.</param>
    /// <param name="editor">Provides the open document of the file.</param>
    /// <param name="filePath">The full path of the file.</param>
    /// <param name="revision">The version to compare with.</param>
    /// <param name="scheduler">Times the quiet period after a change; <see cref="Scheduler.Default"/> when null.</param>
    /// <param name="logger">Receives failures to read the file.</param>
    public GitCompareDocumentViewModel(
        GitSession session,
        IEditorServices? editor,
        string filePath,
        GitFileRevision revision,
        IScheduler? scheduler = null,
        IExtensionLogger? logger = null)
    {
        FilePath = filePath;
        Revision = revision;
        PreviousChangeCommand = AddCommand(ReactiveCommand.Create(
            () => { CurrentHunkIndex--; },
            this.WhenAnyValue(x => x.CurrentHunkIndex, index => index > 0)));
        NextChangeCommand = AddCommand(ReactiveCommand.Create(
            () => { CurrentHunkIndex = Math.Max(0, CurrentHunkIndex + 1); },
            this.WhenAnyValue(x => x.CurrentHunkIndex, x => x.HunkCount, (index, count) => index < count - 1)));
        RefreshCommand = AddCommand(ReactiveCommand.Create(() => _refreshes.OnNext(Unit.Default)));

        _disposables.Add(GitFileDiffSource
            .Observe(session, editor, filePath, revision, scheduler ?? Scheduler.Default, _refreshes, logger)
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(Apply));
        _disposables.Add(this.WhenAnyValue(x => x.IsWholeFile).Skip(1).Subscribe(_ => Rebuild(CurrentHunkIndex)));
        _disposables.Add(_refreshes);
    }

    /// <summary>Gets the full path of the compared file.</summary>
    public string FilePath { get; }

    /// <summary>Gets the version the file is compared with.</summary>
    public GitFileRevision Revision { get; }

    /// <summary>Gets the lines of the diff.</summary>
    [Reactive]
    public partial IReadOnlyList<DiffLine> DiffLines { get; private set; } = [];

    /// <summary>Gets the number of hunks, one per change block.</summary>
    [Reactive]
    public partial int HunkCount { get; private set; }

    /// <summary>Gets or sets the index of the shown hunk, or -1 before the first.</summary>
    [Reactive]
    public partial int CurrentHunkIndex { get; set; } = -1;

    /// <summary>Gets or sets whether every line of the file is shown instead of the changes with their context.</summary>
    [Reactive]
    public partial bool IsWholeFile { get; set; }

    /// <summary>Gets a note instead of the diff while it is computed, without version to compare with and without changes.</summary>
    [Reactive]
    public partial string? Message { get; private set; } = GitStrings.Comparing;

    /// <summary>Goes to the previous change.</summary>
    public ReactiveCommand<RxVoid, RxVoid> PreviousChangeCommand { get; }

    /// <summary>Goes to the next change.</summary>
    public ReactiveCommand<RxVoid, RxVoid> NextChangeCommand { get; }

    /// <summary>Compares again, reading a closed file from disk.</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>
    /// Shows the change at the 1-based <paramref name="line"/> of the file: the first block that ends at or after it,
    /// otherwise the last one. Before the first comparison the line is kept until it arrives.
    /// </summary>
    public void ShowLine(int line)
    {
        if (_diff is null)
        {
            _lineToShow = line;
            return;
        }

        CurrentHunkIndex = BlockAt(_diff.Blocks, line);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposables.Dispose();
    }

    private static int BlockAt(IReadOnlyList<LineDiffBlock> blocks, int line)
    {
        for (int index = 0; index < blocks.Count; index++)
        {
            if (blocks[index].NewStart + Math.Max(blocks[index].NewCount, 1) >= line)
            {
                return index;
            }
        }

        return blocks.Count - 1;
    }

    // A new diff keeps the shown hunk, or shows the line asked for before the first diff.
    private void Apply(GitFileDiff diff)
    {
        _diff = diff;
        int hunk = _lineToShow is { } line ? BlockAt(diff.Blocks, line) : CurrentHunkIndex;
        _lineToShow = null;
        Message = diff.OldLines is null
            ? string.Format(CultureInfo.CurrentCulture, GitStrings.CompareNoVersionFormat, NameOf(Revision))
            : diff.Blocks.Count > 0 ? null
            : diff.IsChanged ? string.Format(CultureInfo.CurrentCulture, GitStrings.CompareOnlyIgnoredBytesFormat, NameOf(Revision))
            : GitStrings.CompareNoChanges;
        Rebuild(hunk);
    }

    // The editor of the view starts every new diff at its top, so the hunk is set again after the lines.
    private void Rebuild(int hunk)
    {
        if (_diff is not { OldLines: { } oldLines } diff)
        {
            DiffLines = [];
            HunkCount = 0;
            return;
        }

        DiffLines = DiffLineBuilder.FromLineDiff(oldLines, diff.NewLines, diff.Blocks, IsWholeFile ? int.MaxValue : CONTEXT_LINES);
        HunkCount = diff.Blocks.Count;
        CurrentHunkIndex = Math.Min(hunk, HunkCount - 1);
    }

    /// <summary>Gets the name of a version in titles and notes: HEAD, or the localized name of the index.</summary>
    internal static string NameOf(GitFileRevision revision) => revision == GitFileRevision.Head ? "HEAD" : GitStrings.IndexName;

    private ReactiveCommand<RxVoid, RxVoid> AddCommand(ReactiveCommand<RxVoid, RxVoid> command)
    {
        _disposables.Add(command);
        // Without a subscriber ReactiveUI ends the application when a command fails.
        _disposables.Add(command.ThrownExceptions.Subscribe(error => Message = error.Message));
        return command;
    }
}
