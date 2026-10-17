using System.Globalization;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

internal sealed partial class GitCommandHandler
{
    public Task StageSelectionAsync(CommandContext context) => ApplySelectionAsync(GitPatchAction.Stage);

    public Task UnstageSelectionAsync(CommandContext context) => ApplySelectionAsync(GitPatchAction.Unstage);

    public Task DiscardSelectionAsync(CommandContext context) => ApplySelectionAsync(GitPatchAction.Discard);

    // The panel box keeps the message body, which the single-line input box of a palette command would drop.
    public async Task CommitAmendAsync(CommandContext context)
    {
        if (!await EnsureRepositoryAsync())
        {
            return;
        }

        await _viewHost.ShowAsync(GitCommands.CHANGES_VIEW_ID, CancellationToken.None);
        if (_panel() is { } panel)
        {
            await Dispatcher.UIThread.InvokeAsync(() => panel.IsAmend = panel.CanAmend);
        }
    }

    /// <summary>
    /// Applies the lines selected in the diff of the Git Changes panel, or the hunk at its cursor. Discarding asks
    /// first, because the discarded lines are not stored anywhere. The refresh afterwards reloads the diff.
    /// </summary>
    private async Task ApplySelectionAsync(GitPatchAction action)
    {
        if (!await EnsureRepositoryAsync())
        {
            return;
        }

        if (_session.Services.Patch is not { } patches)
        {
            await ShowInformationAsync(GitStrings.PatchUnavailable);
            return;
        }

        if (_panel()?.GetDiffSelection(action) is not { } selection)
        {
            await ShowInformationAsync(GitStrings.NoDiffSelection);
            return;
        }

        if (action == GitPatchAction.Discard
            && !await ConfirmAsync(
                string.Format(CultureInfo.CurrentCulture, GitStrings.DiscardSelectionPromptFormat, selection.Path),
                UiStrings.Discard,
                UiStrings.GitDiscardConfirmTitle,
                isDestructive: true))
        {
            return;
        }

        try
        {
            await _session.RunAsync((_, root, ct) => patches.ApplySelectionAsync(root, selection, ct));
        }
        catch (GitPatchRejectedException rejected)
        {
            await ShowInformationAsync(rejected.Reason switch
            {
                GitPatchRejection.DiffChanged => GitStrings.DiffChanged,
                GitPatchRejection.MissingNewline => GitStrings.PatchMissingNewline,
                GitPatchRejection.MultipleFiles => GitStrings.PatchMultipleFiles,
                _ => GitStrings.NoDiffSelection
            });
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
        }
    }
}
