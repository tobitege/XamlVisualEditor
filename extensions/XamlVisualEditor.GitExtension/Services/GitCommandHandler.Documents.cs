using System.Globalization;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

internal sealed partial class GitCommandHandler
{
    public Task ToggleChangeMarkersAsync(CommandContext context) =>
        _changeMarkers?.ToggleAsync() ?? Task.CompletedTask;

    public Task CompareWithHeadAsync(CommandContext context) => CompareAsync(context, GitFileRevision.Head);

    public Task CompareWithIndexAsync(CommandContext context) => CompareAsync(context, GitFileRevision.Index);

    public async Task BlameAsync(CommandContext context)
    {
        if (await ResolveFileAsync(context) is not { } file)
        {
            return;
        }

        await _viewHost.ShowDocumentAsync(
            "git.blame:" + file.FullPath,
            string.Format(CultureInfo.CurrentCulture, GitStrings.BlameTitleFormat, Path.GetFileName(file.FullPath)),
            file.FullPath,
            () => new GitBlameDocumentViewModel(_session, file.RepositoryPath, _executeCommand),
            CancellationToken.None);
    }

    // Argument 1 is the line whose change is shown, as a click on a change mark passes it.
    private async Task CompareAsync(CommandContext context, GitFileRevision revision)
    {
        if (await ResolveFileAsync(context) is not { } file)
        {
            return;
        }

        object? document = await _viewHost.ShowDocumentAsync(
            (revision == GitFileRevision.Head ? "git.compare.head:" : "git.compare.index:") + file.FullPath,
            string.Format(
                CultureInfo.CurrentCulture,
                GitStrings.CompareTitleFormat,
                Path.GetFileName(file.FullPath),
                GitCompareDocumentViewModel.NameOf(revision)),
            file.FullPath,
            () => new GitCompareDocumentViewModel(_session, _editor, file.FullPath, revision, logger: _logger),
            CancellationToken.None);
        if (document is GitCompareDocumentViewModel compare && context.Arguments is [_, int line, ..])
        {
            compare.ShowLine(line);
        }
    }

    private async Task<GitFileTarget?> ResolveFileAsync(CommandContext context)
    {
        if (await ResolveTargetAsync(context) is not { } target || _session.Current.RepositoryRoot is not { } root)
        {
            return null;
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, target.RepositoryPath));
        if (target.RepositoryPath.Length == 0 || Directory.Exists(fullPath))
        {
            await ShowInformationAsync(GitStrings.FileTargetRequired);
            return null;
        }

        return new GitFileTarget(target.RepositoryPath, fullPath);
    }

    private sealed record GitFileTarget(string RepositoryPath, string FullPath);
}
