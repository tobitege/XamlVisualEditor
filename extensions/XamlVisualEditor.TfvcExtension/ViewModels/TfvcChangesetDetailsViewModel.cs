using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.TfvcExtension.ViewModels;

/// <summary>
/// Details of one checked-in changeset: its changed items and its comment, which the author can revise.
/// </summary>
public sealed partial class TfvcChangesetDetailsViewModel : ReactiveObject
{
    private readonly TfvcSession _session;
    private string? _currentUserCollectionUrl;
    private string? _currentUserId;
    private int _requestVersion;

    /// <summary>Creates the view model.</summary>
    public TfvcChangesetDetailsViewModel(TfvcSession session)
    {
        _session = session;
        SaveCommand = ReactiveCommand.CreateFromTask(
            SaveAsync,
            this.WhenAnyValue(x => x.CanEditComment, x => x.IsBusy, x => x.Comment, x => x.Changeset,
                (canEdit, busy, comment, changeset) => canEdit && !busy && changeset is not null && IsChanged(comment, changeset.Comment)));
        CloseCommand = ReactiveCommand.Create(() => { IsActive = false; });
    }

    /// <summary>Raised after a changeset comment was saved on the server.</summary>
    public event EventHandler<TfvcChangesetInfo>? ChangesetUpdated;

    /// <summary>Gets the changed items of the changeset.</summary>
    public ObservableCollection<TfvcChangeRowViewModel> Changes { get; } = new();

    /// <summary>Saves the revised comment.</summary>
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    /// <summary>Hides the changeset details.</summary>
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>Gets the displayed changeset.</summary>
    [Reactive]
    public partial TfvcChangesetInfo? Changeset { get; private set; }

    /// <summary>Gets whether the details replace the pending changes.</summary>
    [Reactive]
    public partial bool IsActive { get; private set; }

    /// <summary>Gets the title of the details.</summary>
    [Reactive]
    public partial string? Title { get; private set; }

    /// <summary>Gets the author and check-in date.</summary>
    [Reactive]
    public partial string? Summary { get; private set; }

    /// <summary>Gets the header of the changed items.</summary>
    [Reactive]
    public partial string? ChangesHeader { get; private set; }

    /// <summary>Gets or sets the edited comment.</summary>
    [Reactive]
    public partial string? Comment { get; set; }

    /// <summary>Gets whether the current user is the author and may revise the comment.</summary>
    [Reactive]
    public partial bool CanEditComment { get; private set; }

    /// <summary>Gets whether a request is running.</summary>
    [Reactive]
    public partial bool IsBusy { get; private set; }

    /// <summary>Gets the status or error message.</summary>
    [Reactive]
    public partial string? Message { get; private set; }

    /// <summary>Shows a changeset and loads its changed items.</summary>
    public async Task ShowAsync(TfvcChangesetInfo changeset, CancellationToken cancellationToken)
    {
        int version = Interlocked.Increment(ref _requestVersion);
        Changeset = changeset;
        Title = string.Format(CultureInfo.CurrentCulture, TfvcStrings.ChangesetTitleFormat, changeset.Id);
        Summary = string.Format(
            CultureInfo.CurrentCulture,
            TfvcStrings.ChangesetSummaryFormat,
            changeset.Author,
            changeset.CreatedDate?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture));
        Comment = changeset.Comment;
        CanEditComment = false;
        Message = null;
        Changes.Clear();
        ChangesHeader = string.Format(CultureInfo.CurrentCulture, TfvcStrings.ChangesetFilesFormat, 0);
        IsActive = true;

        string? collectionUrl = _session.Settings.CollectionUrl;
        if (collectionUrl is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Task<IReadOnlyList<TfvcChangesetChange>> changesTask = _session.Server.GetChangesetChangesAsync(collectionUrl, changeset.Id, cancellationToken);
            string? currentUserId = await GetCurrentUserIdAsync(collectionUrl, cancellationToken);
            IReadOnlyList<TfvcChangesetChange> changes = await changesTask;
            if (version != _requestVersion)
            {
                return;
            }

            CanEditComment = currentUserId is not null
                && string.Equals(currentUserId, changeset.AuthorId, StringComparison.OrdinalIgnoreCase);
            foreach (TfvcChangesetChange change in changes)
            {
                Changes.Add(new TfvcChangeRowViewModel(
                    change.ServerPath,
                    TfvcDisplayText.FormatChangeTypes(TfvcOutputParser.SplitChangeTypes(change.ChangeType))));
            }

            ChangesHeader = string.Format(CultureInfo.CurrentCulture, TfvcStrings.ChangesetFilesFormat, Changes.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _requestVersion)
            {
                Message = TfvcDisplayText.DescribeError(ex);
            }
        }
        finally
        {
            if (version == _requestVersion)
            {
                IsBusy = false;
            }
        }
    }

    private async Task<string?> GetCurrentUserIdAsync(string collectionUrl, CancellationToken cancellationToken)
    {
        if (_currentUserId is null || !string.Equals(_currentUserCollectionUrl, collectionUrl, StringComparison.OrdinalIgnoreCase))
        {
            _currentUserId = await _session.Server.GetCurrentUserIdAsync(collectionUrl, cancellationToken);
            _currentUserCollectionUrl = collectionUrl;
        }

        return _currentUserId;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        TfvcChangesetInfo? changeset = Changeset;
        string? collectionUrl = _session.Settings.CollectionUrl;
        if (changeset is null || collectionUrl is null)
        {
            return;
        }

        string comment = Comment?.Trim() ?? string.Empty;
        if (comment.Length == 0)
        {
            Message = TfvcStrings.ChangesetCommentRequired;
            return;
        }

        IsBusy = true;
        Message = null;
        try
        {
            await _session.Client.UpdateChangesetCommentAsync(collectionUrl, changeset.Id, comment, cancellationToken);
            TfvcChangesetInfo updated = changeset with { Comment = comment };
            Changeset = updated;
            Comment = comment;
            Message = string.Format(CultureInfo.CurrentCulture, TfvcStrings.ChangesetSavedFormat, changeset.Id);
            ChangesetUpdated?.Invoke(this, updated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = TfvcDisplayText.DescribeError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static bool IsChanged(string? comment, string? original)
    {
        return !string.Equals(comment?.Trim() ?? string.Empty, original?.Trim() ?? string.Empty, StringComparison.Ordinal);
    }
}
