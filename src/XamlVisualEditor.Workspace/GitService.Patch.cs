using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.Workspace;

public sealed partial class GitService : IGitPatchService
{
    /// <inheritdoc />
    /// <remarks>
    /// The diff is read again so the patch is built from git's bytes, which keeps line endings and encodings. The patch
    /// goes through <c>git apply --recount</c>: <c>--cached</c> for stage and unstage, <c>--reverse</c> for unstage and
    /// discard. <c>--whitespace=nowarn</c> keeps <c>apply.whitespace</c> from changing or refusing the lines and
    /// <c>--no-ignore-whitespace</c> keeps <c>apply.ignoreWhitespace</c> from loosening the context match. A patch git
    /// rejects changes nothing.
    /// </remarks>
    public async Task ApplySelectionAsync(string repoPath, GitDiffSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        string[] diffArgs = BuildDiffArguments(new GitDiffRequest
        {
            Staged = selection.Action == GitPatchAction.Unstage,
            Path = selection.Path
        });
        GitProcessResult diff = await RunGitAsync(repoPath, diffArgs, GitCommandAccess.Read, ct);
        ThrowIfFailed(diff, diffArgs[0]);
        if (!string.Equals(diff.StandardOutput, selection.ShownDiff, StringComparison.Ordinal))
        {
            throw new GitPatchRejectedException(GitPatchRejection.DiffChanged);
        }

        byte[] patch = GitPatchBuilder.Build(
                diff.StandardOutputBytes,
                selection.FirstLine,
                selection.LineCount,
                reverse: selection.Action != GitPatchAction.Stage)
            ?? throw new GitPatchRejectedException(GitPatchRejection.NothingSelected);
        List<string> args = new() { "apply", "--recount", "--whitespace=nowarn", "--no-ignore-whitespace" };
        if (selection.Action != GitPatchAction.Discard)
        {
            args.Add("--cached");
        }

        if (selection.Action != GitPatchAction.Stage)
        {
            args.Add("--reverse");
        }

        args.Add("-");
        await using Stream input = new MemoryStream(patch, writable: false);
        await RunGitCheckedAsync(repoPath, args, input, ct);
    }

    /// <inheritdoc />
    public async Task AmendAsync(string repoPath, string message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        await using Stream input = GitProcessRequest.Utf8Input(message);
        await RunGitCheckedAsync(repoPath, new[] { "commit", "--amend", "-F", "-" }, input, ct, cancellable: true);
    }
}
