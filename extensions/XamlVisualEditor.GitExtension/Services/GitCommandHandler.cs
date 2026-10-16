using System.Globalization;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>Executes the Git commands registered by <see cref="GitCommands.RegisterCommands"/>.</summary>
internal sealed class GitCommandHandler
{
    private readonly GitSession _session;
    private readonly IExtensionViewHost _viewHost;
    private readonly IWindow _window;
    private readonly IDialogHost _dialogHost;
    private readonly IEditorServices? _editor;
    private readonly IOutputChannel? _outputChannel;
    private readonly Func<GitPanelViewModel?> _panel;
    private readonly IExtensionLogger? _logger;

    /// <summary>Creates the handler.</summary>
    /// <param name="session">The Git session.</param>
    /// <param name="viewHost">Shows and hides the Git Changes panel.</param>
    /// <param name="window">Shows messages and the commit message input box.</param>
    /// <param name="dialogHost">Asks for the confirmation before changes are discarded.</param>
    /// <param name="editor">Provides the active document for path commands without argument.</param>
    /// <param name="outputChannel">The "Git" output channel.</param>
    /// <param name="panel">Returns the Git Changes panel view model once it exists, otherwise null.</param>
    /// <param name="logger">Receives failed commands.</param>
    public GitCommandHandler(
        GitSession session,
        IExtensionViewHost viewHost,
        IWindow window,
        IDialogHost dialogHost,
        IEditorServices? editor,
        IOutputChannel? outputChannel,
        Func<GitPanelViewModel?> panel,
        IExtensionLogger? logger)
    {
        _session = session;
        _viewHost = viewHost;
        _window = window;
        _dialogHost = dialogHost;
        _editor = editor;
        _outputChannel = outputChannel;
        _panel = panel;
        _logger = logger;
    }

    public Task ToggleChangesViewAsync(CommandContext context) =>
        _viewHost.ToggleAsync(GitCommands.CHANGES_VIEW_ID, CancellationToken.None);

    public Task ShowChangesAsync(CommandContext context) =>
        _viewHost.ShowAsync(GitCommands.CHANGES_VIEW_ID, CancellationToken.None);

    public Task RefreshAsync(CommandContext context) => _session.RefreshAsync();

    public Task ShowOutputAsync(CommandContext context)
    {
        _outputChannel?.Show();
        return Task.CompletedTask;
    }

    public Task ToggleAutoRefreshAsync(CommandContext context) =>
        _session.SetWorkingTreeWatcherEnabledAsync(!_session.Current.IsWorkingTreeWatcherEnabled);

    public async Task InitAsync(CommandContext context)
    {
        if (!_session.CanInitializeRepository)
        {
            await _window.ShowErrorMessageAsync(GitStrings.InitUnavailable, CancellationToken.None);
            return;
        }

        try
        {
            GitInitializeResult result = await _session.InitializeRepositoryAsync();
            string message = result.Created
                ? string.Format(CultureInfo.CurrentCulture, GitStrings.InitializedFormat, result.Folder)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    GitStrings.InitAlreadyRepositoryFormat,
                    result.Folder,
                    result.ExistingRepositoryRoot);
            await _window.ShowInformationMessageAsync(message, CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
        }
    }

    public Task StageAsync(CommandContext context) =>
        RunOnTargetAsync(
            context,
            GitCommandTarget.HasWorkingTreeChange,
            GitStrings.NoChangesToStageFormat,
            (git, root, paths, ct) => git.StageAsync(root, paths, ct));

    public Task UnstageAsync(CommandContext context) =>
        RunOnTargetAsync(
            context,
            GitCommandTarget.HasStagedChange,
            GitStrings.NoChangesToUnstageFormat,
            (git, root, paths, ct) => git.UnstageAsync(root, paths, ct));

    public async Task DiscardAsync(CommandContext context)
    {
        if (await ResolveTargetAsync(context) is not { } target)
        {
            return;
        }

        IReadOnlyList<GitFileChange> changes = GitCommandTarget.FindChanges(
            _session.Current.Changes,
            target.RepositoryPath,
            GitCommandTarget.CanDiscard);
        if (changes.Count == 0)
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.NoChangesToDiscardFormat, target.DisplayPath));
            return;
        }

        string message = changes.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, UiStrings.GitDiscardWorkingChangesPrompt, changes[0].Path)
            : string.Format(CultureInfo.CurrentCulture, GitStrings.DiscardManyPromptFormat, changes.Count, target.DisplayPath);
        bool? confirmed = await _dialogHost.ShowDialogAsync<bool>(
            ConfirmationDialogRequest.DialogId,
            new ConfirmationDialogRequest(UiStrings.GitDiscardConfirmTitle, message, UiStrings.Discard, true),
            CancellationToken.None);
        if (confirmed != true)
        {
            return;
        }

        string[] paths = PathsOf(changes);
        await RunGitAsync((git, root, ct) => git.DiscardAsync(root, paths, ct));
    }

    public async Task StageAllAsync(CommandContext context)
    {
        IReadOnlyList<GitFileChange> changes = GitCommandTarget.FindChanges(
            _session.Current.Changes,
            string.Empty,
            GitCommandTarget.HasWorkingTreeChange);
        if (changes.Count == 0)
        {
            await ShowInformationAsync(GitStrings.NothingToStage);
            return;
        }

        string[] paths = PathsOf(changes);
        await RunGitAsync((git, root, ct) => git.StageAsync(root, paths, ct));
    }

    public async Task UnstageAllAsync(CommandContext context)
    {
        IReadOnlyList<GitFileChange> changes = GitCommandTarget.FindChanges(
            _session.Current.Changes,
            string.Empty,
            GitCommandTarget.HasStagedChange);
        if (changes.Count == 0)
        {
            await ShowInformationAsync(GitStrings.NothingToUnstage);
            return;
        }

        string[] paths = PathsOf(changes);
        await RunGitAsync((git, root, ct) => git.UnstageAsync(root, paths, ct));
    }

    public async Task CommitAsync(CommandContext context)
    {
        GitSnapshot snapshot = _session.Current;
        if (!snapshot.IsRepository)
        {
            await ShowInformationAsync(snapshot.ErrorMessage ?? UiStrings.WorkspaceNotGitRepository);
            return;
        }

        if (GitCommandTarget.FindChanges(snapshot.Changes, string.Empty, GitCommandTarget.HasStagedChange).Count == 0)
        {
            await ShowInformationAsync(GitStrings.NothingToCommit);
            return;
        }

        GitPanelViewModel? panel = _panel();
        string? message = panel?.CommitMessage?.Trim();
        bool fromPanel = !string.IsNullOrWhiteSpace(message);
        if (!fromPanel)
        {
            message = (await _window.ShowInputBoxAsync(
                new InputBoxOptions(GitStrings.CommitInputTitle, GitStrings.CommitInputPrompt, null),
                CancellationToken.None))?.Trim();
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string commitMessage = message;
        if (await RunGitAsync((git, root, ct) => git.CommitAsync(root, commitMessage, ct)) && fromPanel && panel is not null)
        {
            ClearCommitMessage(panel);
        }
    }

    private async Task RunOnTargetAsync(
        CommandContext context,
        Func<GitFileChange, bool> predicate,
        string noChangesFormat,
        Func<IGitService, string, IReadOnlyList<string>, CancellationToken, Task> operation)
    {
        if (await ResolveTargetAsync(context) is not { } target)
        {
            return;
        }

        IReadOnlyList<GitFileChange> changes = GitCommandTarget.FindChanges(
            _session.Current.Changes,
            target.RepositoryPath,
            predicate);
        if (changes.Count == 0)
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, noChangesFormat, target.DisplayPath));
            return;
        }

        string[] paths = PathsOf(changes);
        await RunGitAsync((git, root, ct) => operation(git, root, paths, ct));
    }

    private async Task<GitCommandTargetPath?> ResolveTargetAsync(CommandContext context)
    {
        GitSnapshot snapshot = _session.Current;
        if (!snapshot.IsRepository || snapshot.RepositoryRoot is null)
        {
            await ShowInformationAsync(snapshot.ErrorMessage ?? UiStrings.WorkspaceNotGitRepository);
            return null;
        }

        string? path = GitCommandTarget.GetPath(context, _editor);
        if (path is null)
        {
            await ShowInformationAsync(GitStrings.NoTargetPath);
            return null;
        }

        string? repositoryPath = GitCommandTarget.ToRepositoryPath(snapshot.RepositoryRoot, path);
        if (repositoryPath is null)
        {
            await ShowInformationAsync(string.Format(
                CultureInfo.CurrentCulture,
                GitStrings.PathOutsideRepositoryFormat,
                path,
                snapshot.RepositoryRoot));
            return null;
        }

        return new GitCommandTargetPath(repositoryPath, repositoryPath.Length == 0 ? snapshot.RepositoryRoot : repositoryPath);
    }

    /// <summary>Runs a git command through the session; returns false and reports git's error when it failed.</summary>
    private async Task<bool> RunGitAsync(Func<IGitService, string, CancellationToken, Task> operation)
    {
        try
        {
            await _session.RunAsync(operation);
            return true;
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
            return false;
        }
    }

    private async Task ReportFailureAsync(InvalidOperationException exception)
    {
        string message = string.Format(CultureInfo.CurrentCulture, UiStrings.GitCommandFailed, exception.Message);
        _logger?.Error(message);
        await _window.ShowErrorMessageAsync(message, CancellationToken.None);
    }

    private Task ShowInformationAsync(string message) =>
        _window.ShowInformationMessageAsync(message, CancellationToken.None);

    private static void ClearCommitMessage(GitPanelViewModel panel)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            panel.CommitMessage = string.Empty;
        }
        else
        {
            Dispatcher.UIThread.Post(() => panel.CommitMessage = string.Empty);
        }
    }

    private static string[] PathsOf(IReadOnlyList<GitFileChange> changes)
    {
        string[] paths = new string[changes.Count];
        for (int i = 0; i < changes.Count; i++)
        {
            paths[i] = changes[i].Path;
        }

        return paths;
    }

    private sealed record GitCommandTargetPath(string RepositoryPath, string DisplayPath);
}
