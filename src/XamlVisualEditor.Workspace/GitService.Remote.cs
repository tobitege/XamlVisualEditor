using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Workspace;

public sealed partial class GitService
{
    /// <summary>The SSH command of network commands when the user configured none: SSH fails instead of asking.</summary>
    internal const string BATCH_MODE_SSH_COMMAND = "ssh -o BatchMode=yes";

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitRemote>> GetRemotesAsync(string repoPath, CancellationToken ct = default)
    {
        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "remote", "-v" },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        return result.ExitCode == 0
            ? GitRefParser.ParseRemotes(result.StandardOutput)
            : Array.Empty<GitRemote>();
    }

    /// <inheritdoc />
    public Task AddRemoteAsync(string repoPath, string name, string url, CancellationToken ct = default)
    {
        ThrowIfOptionLike(name, nameof(name));
        return RunRemoteCommandAsync(repoPath, "add", name, url, ct);
    }

    /// <inheritdoc />
    public Task RenameRemoteAsync(string repoPath, string name, string newName, CancellationToken ct = default)
    {
        ThrowIfOptionLike(newName, nameof(newName));
        return RunRemoteCommandAsync(repoPath, "rename", name, newName, ct);
    }

    // git accepts such a name, and a command that passes it without "--", in this or another program, reads it as an
    // option, for example --upload-pack=<command> in git fetch.
    private static void ThrowIfOptionLike(string name, string parameterName)
    {
        if (name.StartsWith('-'))
        {
            throw new ArgumentException("A remote name must not start with '-'.", parameterName);
        }
    }

    /// <inheritdoc />
    public Task RemoveRemoteAsync(string repoPath, string name, CancellationToken ct = default) =>
        RunRemoteCommandAsync(repoPath, "remove", name, null, ct);

    /// <inheritdoc />
    public Task SetRemoteUrlAsync(string repoPath, string name, string url, CancellationToken ct = default) =>
        RunRemoteCommandAsync(repoPath, "set-url", name, url, ct);

    // The end of the options keeps a name or URL that starts with "-" from being read as an option.
    private Task RunRemoteCommandAsync(string repoPath, string command, string name, string? value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        List<string> args = new() { "remote", command, "--", name };
        if (value is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            args.Add(value);
        }

        return RunGitCheckedAsync(repoPath, args, standardInput: null, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Each remote is fetched with <c>git fetch &lt;remote&gt;</c>, so its configured refspecs, tag following and the
    /// pruning of <c>fetch.prune</c>, <c>remote.&lt;name&gt;.prune</c> and <c>fetch.pruneTags</c> apply as on the
    /// command line. When the configuration prunes, remote-tracking branches deleted on the remote disappear and the
    /// local branches that tracked them show their upstream as gone. A remote with
    /// <c>remote.&lt;name&gt;.skipFetchAll</c> is skipped, as by <c>git fetch --all</c>.
    /// </remarks>
    public async Task<GitRemoteResult> FetchAsync(string repoPath, CancellationToken ct = default)
    {
        IReadOnlyList<string> remotes = await GetRemoteNamesAsync(repoPath, ct);
        IReadOnlySet<string> skipped = await ReadSkipFetchAllRemotesAsync(repoPath, ct);
        IReadOnlyDictionary<string, string?> environment = await CreateNetworkEnvironmentAsync(repoPath, ct);
        List<string> fetched = new();
        List<string> reports = new();
        List<string> failures = new();
        GitRemoteFailureKind failureKind = GitRemoteFailureKind.None;
        foreach (string remote in remotes)
        {
            if (skipped.Contains(remote))
            {
                continue;
            }

            // A remote name that starts with "-", which git accepts, must not become an option.
            GitRemoteResult result = await RunNetworkCommandAsync(repoPath, new[] { "fetch", "--", remote }, environment, ct);
            if (result.Success)
            {
                fetched.Add(remote);
                if (result.Message.Length > 0)
                {
                    reports.Add(result.Message);
                }
            }
            else
            {
                if (failureKind == GitRemoteFailureKind.None)
                {
                    failureKind = result.FailureKind;
                }

                failures.Add(remote + ": " + result.Message);
            }
        }

        return new GitRemoteResult
        {
            FailureKind = failureKind,
            Message = string.Join(Environment.NewLine, failureKind == GitRemoteFailureKind.None ? reports : failures),
            FetchedRemotes = fetched
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs the fetch of <c>git pull</c> as network command: <c>git fetch &lt;remote&gt;</c> with the remote of the
    /// upstream, so its configured refspecs, tag following and pruning apply as for <see cref="FetchAsync"/>. Because
    /// the remote is the one of the current branch, git also fetches the values of <c>branch.&lt;name&gt;.merge</c>
    /// and marks them for merge in <c>FETCH_HEAD</c>. The upstream is the first of them, as for <c>@{upstream}</c>;
    /// without the entry for it the upstream branch no longer exists on the remote, and the pull fails with
    /// <see cref="GitRemoteFailureKind.UpstreamGone"/> and the message of <c>git pull</c>, whether or not the
    /// configuration prunes. Entries for other references do not count. A fetch of another remote that ends between
    /// this fetch and the reading of <c>FETCH_HEAD</c> replaces the file, and the pull then reports the upstream as
    /// gone, unless that fetch wrote an entry of the same description, such as a branch of the same name. Then
    /// <c>git rev-parse --verify @{upstream}</c> resolves the remote-tracking branch that the fetch updated to a
    /// commit, <c>git merge --ff-only</c> merges that commit as writing command, and the pulled commits are counted
    /// from <c>ORIG_HEAD</c>, which the merge sets to the commit it started from, also when the branch is up to date,
    /// to that commit. A fetch while the merge waits for other writing commands replaces <c>FETCH_HEAD</c>, so the
    /// commit does not come from there; neither a commit nor a fetch after the merge changes the count. A second pull
    /// of the same repository that merges between this merge and the count moves <c>ORIG_HEAD</c>; only the reported
    /// count is then wrong. The reflog entry of the merge reads <c>pull: Fast-forward</c>. Only fast-forwards: a merge
    /// or rebase can stop with conflicts, which the editor does not resolve yet. Cancellation still stops the wait for
    /// other writing commands, but not the merge itself, because killing git while it updates the working tree can
    /// leave <c>index.lock</c> behind.
    /// </remarks>
    public async Task<GitRemoteResult> PullAsync(string repoPath, CancellationToken ct = default)
    {
        const string BRANCH_PREFIX = "refs/heads/";
        GitProcessResult head = await RunGitAsync(
            repoPath,
            new[] { "symbolic-ref", "-q", "HEAD" },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        string reference = head.StandardOutput.Trim();
        if (head.ExitCode != 0 || !reference.StartsWith(BRANCH_PREFIX, StringComparison.Ordinal))
        {
            // The message of git pull on a detached HEAD.
            return GitRemoteResult.Failed(GitRemoteFailureKind.Other, "You are not currently on a branch.");
        }

        // The values git uses for @{upstream}, also for an unborn branch: the last branch.<name>.remote and the first
        // branch.<name>.merge.
        string branch = reference[BRANCH_PREFIX.Length..];
        string? remote = await ReadConfigValueAsync(repoPath, "--get", "branch." + branch + ".remote", ct);
        string? merge = await ReadConfigValueAsync(repoPath, "--get-all", "branch." + branch + ".merge", ct);
        if (remote is null || merge is null)
        {
            // The message of git pull for a branch without upstream.
            return GitRemoteResult.Failed(
                GitRemoteFailureKind.NoUpstream,
                "There is no tracking information for the current branch.");
        }

        // git pull also pulls into an unborn branch, such as the branch of a clone of an empty repository.
        bool unborn = !await ReferenceExistsAsync(repoPath, reference, ct);
        IReadOnlyDictionary<string, string?> environment = await CreateNetworkEnvironmentAsync(repoPath, ct);
        GitRemoteResult fetched = await RunNetworkCommandAsync(
            repoPath,
            // A remote name of the configuration that starts with '-' stays an argument instead of becoming an option.
            new[] { "fetch", "--", remote },
            environment,
            ct);
        if (!fetched.Success)
        {
            return fetched;
        }

        if (!await FetchedMergeReferenceAsync(repoPath, merge, ct))
        {
            // The message of git pull when the fetch did not bring the upstream branch.
            return GitRemoteResult.Failed(
                GitRemoteFailureKind.UpstreamGone,
                $"Your configuration specifies to merge with the ref '{merge}' from the remote, "
                    + "but no such ref was fetched.");
        }

        GitProcessResult upstream = await RunGitAsync(
            repoPath,
            new[] { "rev-parse", "--verify", "@{upstream}" },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true,
            environment: UntranslatedOutput);
        if (upstream.ExitCode != 0)
        {
            // rev-parse names the cause as git merge does when the upstream is not configured or not stored as a
            // remote-tracking branch, but only "Needed a single revision" when its remote-tracking branch does not
            // exist, for which git merge writes the message below.
            return upstream.StandardError.Contains("Needed a single revision", StringComparison.Ordinal)
                ? GitRemoteResult.Failed(
                    GitRemoteFailureKind.UpstreamGone,
                    "merge: @{upstream} - not something we can merge")
                : ToRemoteResult(upstream, "rev-parse");
        }

        string commit = upstream.StandardOutput.Trim();
        string[] args = { "merge", "--ff-only", commit };
        // The reflog entry reads "pull: Fast-forward" as for git pull instead of naming the commit.
        Dictionary<string, string?> mergeEnvironment = new(UntranslatedOutput) { ["GIT_REFLOG_ACTION"] = "pull" };
        ct.ThrowIfCancellationRequested();
        GitProcessResult merged = await RunGitAsync(
            repoPath,
            args,
            GitCommandAccess.Write,
            CancellationToken.None,
            allowNonZeroExitCode: true,
            environment: mergeEnvironment,
            waitCancellation: ct);
        GitRemoteResult result = ToRemoteResult(merged, args[0]);
        if (!result.Success)
        {
            return result;
        }

        return new GitRemoteResult
        {
            Message = result.Message,
            // A merge into an unborn branch sets no ORIG_HEAD, and one left by an earlier command would be wrong.
            // Counted up to the merged commit, which neither a commit nor a fetch after the merge moves.
            PulledCommitCount = unborn ? null : await CountCommitsAsync(repoPath, "ORIG_HEAD.." + commit)
        };
    }

    /// <summary>
    /// Returns the first value that <c>git config -z <paramref name="mode"/> <paramref name="key"/></c> prints, or
    /// null when the key is not set: <c>--get</c> prints the last value, <c>--get-all</c> all values in order.
    /// </summary>
    private async Task<string?> ReadConfigValueAsync(string repoPath, string mode, string key, CancellationToken ct)
    {
        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "config", "-z", mode, key },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        string value = result.StandardOutput.Split('\0')[0];
        return result.ExitCode == 0 && value.Length > 0 ? value : null;
    }

    /// <summary>
    /// Returns whether <c>FETCH_HEAD</c> of the working tree has the entry marked for merge that the fetch writes for
    /// the remote reference <paramref name="merge"/>.
    /// </summary>
    private async Task<bool> FetchedMergeReferenceAsync(string repoPath, string merge, CancellationToken ct)
    {
        if (await GetDirectoriesAsync(repoPath, ct) is not { } directories)
        {
            return false;
        }

        string fetchHead;
        try
        {
            fetchHead = await File.ReadAllTextAsync(Path.Combine(directories.GitDirectory, "FETCH_HEAD"), ct);
        }
        catch (IOException)
        {
            return false;
        }

        return HasMergeEntry(fetchHead, merge);
    }

    /// <summary>
    /// Returns whether <paramref name="fetchHead"/> has a line marked for merge, whose second tab-separated field is
    /// empty instead of <c>not-for-merge</c>, with the description that git fetch writes for the remote reference
    /// <paramref name="merge"/>: <c>branch '&lt;name&gt;' of </c> for <c>refs/heads/</c>, <c>tag '&lt;name&gt;' of </c>
    /// for <c>refs/tags/</c>, <c>remote-tracking branch '&lt;name&gt;' of </c> for <c>refs/remotes/</c> and
    /// <c>'&lt;reference&gt;' of </c> for any other reference, each followed by the URL. Lines for other references,
    /// such as those of a fetch of another remote, do not count. For <c>HEAD</c>, which git describes by the URL
    /// alone, any line marked for merge counts; a fetch from a remote whose HEAD names a missing branch writes none. A
    /// merge value that is not a full reference name never matches, because git writes the name of the remote
    /// reference.
    /// </summary>
    internal static bool HasMergeEntry(string fetchHead, string merge)
    {
        string expected = merge == "HEAD" ? string.Empty : FetchHeadName(merge);
        return fetchHead.Split('\n').Any(line => line.Split('\t') is [{ Length: > 0 }, "", string description, ..]
            && description.StartsWith(expected, StringComparison.Ordinal));
    }

    // The kinds of references that git fetch names in FETCH_HEAD by the part after their prefix.
    private static readonly (string Prefix, string Name)[] FetchHeadKinds =
    {
        ("refs/heads/", "branch"),
        ("refs/tags/", "tag"),
        ("refs/remotes/", "remote-tracking branch")
    };

    // The description that git fetch writes in FETCH_HEAD for a reference other than HEAD, up to the URL.
    private static string FetchHeadName(string reference)
    {
        foreach ((string prefix, string name) in FetchHeadKinds)
        {
            if (reference.StartsWith(prefix, StringComparison.Ordinal))
            {
                return $"{name} '{reference[prefix.Length..]}' of ";
            }
        }

        return $"'{reference}' of ";
    }

    /// <summary>Returns the number of commits in <paramref name="range"/>, or null when git cannot count them.</summary>
    private async Task<int?> CountCommitsAsync(string repoPath, string range)
    {
        GitProcessResult counted = await RunGitAsync(
            repoPath,
            new[] { "rev-list", "--count", range },
            GitCommandAccess.Read,
            CancellationToken.None,
            allowNonZeroExitCode: true);
        return counted.ExitCode == 0
            && int.TryParse(counted.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            ? count
            : null;
    }

    /// <summary>Reads the remotes with <c>remote.&lt;name&gt;.skipFetchAll</c> set to true.</summary>
    private async Task<IReadOnlySet<string>> ReadSkipFetchAllRemotesAsync(string repoPath, CancellationToken ct)
    {
        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "config", "-z", "--get-regexp", @"^remote\..*\.skipfetchall$" },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        return result.ExitCode == 0
            ? ParseSkipFetchAllRemotes(result.StandardOutput)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the remotes whose <c>remote.&lt;name&gt;.skipfetchall</c> is true in the output of
    /// <c>git config -z --get-regexp</c>: entries end with NUL, a line feed separates the key from the value, a key
    /// without value is a true boolean, and the last entry of a remote wins.
    /// </summary>
    internal static IReadOnlySet<string> ParseSkipFetchAllRemotes(string output)
    {
        const string SECTION = "remote.";
        const string VARIABLE = ".skipfetchall";
        Dictionary<string, bool> skipFetchAll = new(StringComparer.Ordinal);
        foreach (string entry in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int newline = entry.IndexOf('\n', StringComparison.Ordinal);
            string key = newline < 0 ? entry : entry[..newline];
            string? value = newline < 0 ? null : entry[(newline + 1)..];
            if (key.StartsWith(SECTION, StringComparison.Ordinal)
                && key.EndsWith(VARIABLE, StringComparison.Ordinal)
                && key.Length > SECTION.Length + VARIABLE.Length)
            {
                skipFetchAll[key[SECTION.Length..^VARIABLE.Length]] = IsTrue(value);
            }
        }

        return skipFetchAll.Where(entry => entry.Value).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
    }

    // Git's boolean values: a key without value, true, yes, on or a number other than 0.
    private static bool IsTrue(string? value)
    {
        if (value is null)
        {
            return true;
        }

        string trimmed = value.Trim();
        return trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase)
            || (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) && number != 0);
    }

    /// <inheritdoc />
    public Task<GitRemoteResult> PushAsync(string repoPath, string? setUpstreamRemote = null, CancellationToken ct = default)
    {
        string[] args = setUpstreamRemote is null
            ? new[] { "push" }
            : new[] { "push", "--set-upstream", "--", setUpstreamRemote, "HEAD" };
        return RunNetworkCommandAsync(repoPath, args, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The refspec and the lease name the branch, its upstream and the expected commit, so neither the configuration nor
    /// a branch checked out meanwhile decides what is replaced; a plain <c>--force-with-lease</c> would push every
    /// branch that <c>push.default=matching</c> matches and compare with remote-tracking branches a fetch moved.
    /// </remarks>
    public async Task<GitRemoteResult> ForcePushAsync(
        string repoPath,
        string branch,
        string expectedUpstreamSha,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedUpstreamSha);
        string? remote = await ReadConfigAsync(repoPath, "branch." + branch + ".remote", ct);
        string? upstream = await ReadConfigAsync(repoPath, "branch." + branch + ".merge", ct);
        // The remote "." is the repository itself: its upstream is a local branch, which a force push would rewrite.
        if (remote is null or "." || upstream is null)
        {
            return GitRemoteResult.Failed(GitRemoteFailureKind.NoUpstream, $"The branch {branch} has no upstream branch on a remote.");
        }

        return await RunNetworkCommandAsync(
            repoPath,
            ["push", "--force-with-lease=" + upstream + ":" + expectedUpstreamSha, "--", remote, LOCAL_BRANCH_PREFIX + branch + ":" + upstream],
            ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>@{push}</c> names the remote-tracking branch a plain <c>git push</c> of the branch updates, <c>@{upstream}</c>
    /// the one <see cref="ForcePushAsync"/> replaces; when both are the same, the push of the branch went there. git
    /// fails to resolve <c>@{push}</c> when <c>push.default=simple</c> and the names differ. Under
    /// <c>push.default=matching</c> a push updates every branch of the same name, so the rejection can come from
    /// another branch; no lease is given then.
    /// </remarks>
    public async Task<string?> GetForcePushLeaseAsync(string repoPath, string branch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        if (await ReadConfigAsync(repoPath, "branch." + branch + ".remote", ct) is null or "."
            || string.Equals(await ReadConfigAsync(repoPath, "push.default", ct), "matching", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? push = await ReadRevisionAsync(repoPath, ["--symbolic-full-name", branch + "@{push}"], ct);
        string? upstream = await ReadRevisionAsync(repoPath, ["--symbolic-full-name", branch + "@{upstream}"], ct);
        if (push is null || push != upstream)
        {
            return null;
        }

        return await ReadRevisionAsync(repoPath, ["--verify", "--quiet", upstream], ct);
    }

    /// <summary>Returns the output of <c>git rev-parse</c> with <paramref name="args"/>, or null when it fails.</summary>
    private async Task<string?> ReadRevisionAsync(string repoPath, string[] args, CancellationToken ct)
    {
        GitProcessResult result = await RunGitAsync(repoPath, ["rev-parse", .. args], GitCommandAccess.Read, ct, allowNonZeroExitCode: true);
        string value = result.StandardOutput.Trim();
        return result.ExitCode == 0 && value.Length > 0 ? value : null;
    }

    /// <summary>Returns the value of a git configuration key, or null when it is not set.</summary>
    private async Task<string?> ReadConfigAsync(string repoPath, string key, CancellationToken ct)
    {
        GitProcessResult result = await RunGitAsync(repoPath, ["config", "--get", key], GitCommandAccess.Read, ct, allowNonZeroExitCode: true);
        string value = result.StandardOutput.Trim();
        return result.ExitCode == 0 && value.Length > 0 ? value : null;
    }

    /// <inheritdoc />
    /// <remarks>The full reference name keeps a branch of the same name from being pushed instead.</remarks>
    public Task<GitRemoteResult> PushTagAsync(string repoPath, string remote, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunNetworkCommandAsync(repoPath, new[] { "push", "--", remote, TAG_PREFIX + name }, ct);
    }

    /// <inheritdoc />
    public Task<GitRemoteResult> DeleteRemoteTagAsync(string repoPath, string remote, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunNetworkCommandAsync(repoPath, new[] { "push", "--", remote, ":" + TAG_PREFIX + name }, ct);
    }

    /// <summary>
    /// Returns whether network commands set <see cref="BATCH_MODE_SSH_COMMAND"/>: only when the user configured no
    /// SSH program through <c>GIT_SSH</c>, <c>GIT_SSH_COMMAND</c> or <c>core.sshCommand</c>, so Plink, TortoisePlink
    /// and own SSH settings stay in effect.
    /// </summary>
    /// <param name="processVariable">Reads a variable of the environment the editor runs in.</param>
    /// <param name="overlay">The variables of <see cref="GitProcessOptions.Environment"/>; a null value removes a variable.</param>
    /// <param name="configuredSshCommand">The value of <c>core.sshCommand</c>, or null when it is not set.</param>
    internal static bool UsesBatchModeSsh(
        Func<string, string?> processVariable,
        IReadOnlyDictionary<string, string?> overlay,
        string? configuredSshCommand)
    {
        foreach (string name in new[] { "GIT_SSH", "GIT_SSH_COMMAND" })
        {
            string? value = overlay.TryGetValue(name, out string? overlaid) ? overlaid : processVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
        }

        return string.IsNullOrWhiteSpace(configuredSshCommand);
    }

    /// <summary>Returns the environment variables of the network commands of <paramref name="repoPath"/>.</summary>
    internal async Task<IReadOnlyDictionary<string, string?>> CreateNetworkEnvironmentAsync(string repoPath, CancellationToken ct)
    {
        Dictionary<string, string?> environment = new(UntranslatedOutput);
        GitProcessResult sshCommand = await RunGitAsync(
            repoPath,
            new[] { "config", "--get", "core.sshCommand" },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        string? configured = sshCommand.ExitCode == 0 ? sshCommand.StandardOutput.Trim() : null;
        if (UsesBatchModeSsh(Environment.GetEnvironmentVariable, _options.Environment, configured))
        {
            environment["GIT_SSH_COMMAND"] = BATCH_MODE_SSH_COMMAND;
        }

        return environment;
    }

    private async Task<GitRemoteResult> RunNetworkCommandAsync(string repoPath, string[] args, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string?> environment = await CreateNetworkEnvironmentAsync(repoPath, ct);
        return await RunNetworkCommandAsync(repoPath, args, environment, ct);
    }

    private async Task<GitRemoteResult> RunNetworkCommandAsync(
        string repoPath,
        string[] args,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken ct)
    {
        GitProcessResult result;
        try
        {
            result = await RunGitAsync(
                repoPath,
                args,
                GitCommandAccess.Network,
                ct,
                allowNonZeroExitCode: true,
                environment: environment,
                timeout: _options.NetworkTimeout);
        }
        catch (TimeoutException exception)
        {
            return GitRemoteResult.Failed(GitRemoteFailureKind.Timeout, exception.Message);
        }

        return ToRemoteResult(result, args[0]);
    }

    private static GitRemoteResult ToRemoteResult(GitProcessResult result, string command)
    {
        string error = result.StandardError.Trim();
        string output = result.StandardOutput.Trim();
        if (result.ExitCode == 0)
        {
            // Fetch and push report their ref updates on the error output.
            return GitRemoteResult.Succeeded(error.Length == 0 ? output : error);
        }

        string message = error.Length == 0 ? output : error;
        if (message.Length == 0)
        {
            message = $"git {command} exited with code {result.ExitCode}.";
        }

        return GitRemoteResult.Failed(GitRemoteErrorClassifier.Classify(message), message);
    }
}
