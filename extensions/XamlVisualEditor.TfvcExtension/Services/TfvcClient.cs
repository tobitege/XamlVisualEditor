using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.TfvcExtension.Models;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>Workspace operations of Team Foundation Version Control.</summary>
public interface ITfvcClient
{
    /// <summary>Gets or sets an explicit tf.exe path; null selects automatic detection.</summary>
    string? ConfiguredTfExePath { get; set; }

    /// <summary>Lists the workspaces of the current user on this computer.</summary>
    Task<IReadOnlyList<TfvcWorkspace>> GetWorkspacesAsync(string collectionUrl, CancellationToken cancellationToken);

    /// <summary>Lists all pending changes of a workspace.</summary>
    Task<IReadOnlyList<TfvcPendingChange>> GetPendingChangesAsync(string collectionUrl, TfvcWorkspace workspace, CancellationToken cancellationToken);

    /// <summary>Lists the pending changes of one local item.</summary>
    Task<IReadOnlyList<TfvcPendingChange>> GetPendingChangesForLocalPathAsync(string localPath, CancellationToken cancellationToken);

    /// <summary>Checks out local files for edit; with <paramref name="recursive"/> folders include all items below them.</summary>
    Task<TfCommandResult> CheckOutAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, bool recursive, CancellationToken cancellationToken);

    /// <summary>Adds local files to version control.</summary>
    Task<TfCommandResult> AddAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, CancellationToken cancellationToken);

    /// <summary>Adds local folders with every item below them that version control does not ignore.</summary>
    Task<TfCommandResult> AddFoldersAsync(TfvcWorkspace workspace, IReadOnlyList<string> localFolders, CancellationToken cancellationToken);

    /// <summary>Pends the deletion of files.</summary>
    Task<TfCommandResult> DeleteAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the local paths below a folder that differ from the workspace without a pending change: added, deleted,
    /// or changed without check-out. Ignored items are left out.
    /// </summary>
    Task<IReadOnlyCollection<string>> PreviewLocalChangesAsync(TfvcWorkspace workspace, string localFolder, CancellationToken cancellationToken);

    /// <summary>Undoes pending changes of the given items.</summary>
    Task<TfCommandResult> UndoAsync(TfvcWorkspace workspace, IReadOnlyList<string> itemSpecs, bool recursive, CancellationToken cancellationToken);

    /// <summary>Checks in pending changes; an empty item list checks in every pending change of the workspace.</summary>
    Task<TfCommandResult> CheckInAsync(TfvcWorkspace workspace, string comment, IReadOnlyList<string> itemSpecs, CancellationToken cancellationToken);

    /// <summary>Shelves pending changes; an empty item list shelves every pending change of the workspace.</summary>
    Task<TfCommandResult> ShelveAsync(TfvcWorkspace workspace, string shelvesetName, string? comment, IReadOnlyList<string> itemSpecs, CancellationToken cancellationToken);

    /// <summary>Gets the latest version of an item.</summary>
    Task<TfCommandResult> GetLatestAsync(TfvcWorkspace workspace, string itemSpec, bool recursive, CancellationToken cancellationToken);

    /// <summary>Compares a local item with its workspace version as unified diff.</summary>
    Task<TfCommandResult> DifferenceAsync(TfvcWorkspace workspace, string localPath, CancellationToken cancellationToken);

    /// <summary>Detects the collection URL of the workspace that maps a local directory.</summary>
    Task<string?> DetectCollectionUrlAsync(string localDirectory, CancellationToken cancellationToken);

    /// <summary>Replaces the comment of a checked-in changeset.</summary>
    Task<TfCommandResult> UpdateChangesetCommentAsync(string collectionUrl, int changesetId, string comment, CancellationToken cancellationToken);
}

/// <summary>Raised when a tf.exe command reports a failure.</summary>
public sealed class TfCommandException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TfCommandException(string command, TfCommandResult result)
        : base(result.CombinedOutput)
    {
        Command = command;
        Result = result;
    }

    /// <summary>Gets the tf.exe command name.</summary>
    public string Command { get; }

    /// <summary>Gets the command result.</summary>
    public TfCommandResult Result { get; }
}

/// <summary>Raised when the item list of one tf.exe call exceeds the command line limit.</summary>
public sealed class TfCommandLineTooLongException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TfCommandLineTooLongException()
        : base("The tf.exe command line is too long.")
    {
    }
}

/// <summary>Implements workspace operations on top of tf.exe.</summary>
public sealed class TfvcClient : ITfvcClient
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ChangeTimeout = TimeSpan.FromMinutes(10);

    // The byte order mark states the encoding of non-ASCII comments explicitly.
    private static readonly Encoding CommentFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private readonly ITfCommandRunner _runner;

    /// <summary>Creates the client.</summary>
    public TfvcClient(ITfCommandRunner runner)
    {
        _runner = runner;
    }

    /// <inheritdoc />
    public string? ConfiguredTfExePath
    {
        get => _runner.ConfiguredTfExePath;
        set => _runner.ConfiguredTfExePath = value;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TfvcWorkspace>> GetWorkspacesAsync(string collectionUrl, CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "workspaces",
            "/format:xml",
            "/computer:" + Environment.MachineName,
            "/collection:" + collectionUrl
        ];
        TfCommandResult result = await RunQueryAsync(GetNeutralDirectory(), arguments, cancellationToken).ConfigureAwait(false);
        return TfvcOutputParser.ParseWorkspaces(result.StandardOutput);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TfvcPendingChange>> GetPendingChangesAsync(
        string collectionUrl,
        TfvcWorkspace workspace,
        CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "status",
            "/collection:" + collectionUrl,
            "/workspace:" + workspace.Name,
            "/recursive",
            "/format:xml",
            "/noprompt",
            "$/"
        ];
        TfCommandResult result = await RunQueryAsync(GetWorkingDirectory(workspace), arguments, cancellationToken).ConfigureAwait(false);
        return TfvcOutputParser.ParsePendingChanges(result.StandardOutput, workspace.Name);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TfvcPendingChange>> GetPendingChangesForLocalPathAsync(string localPath, CancellationToken cancellationToken)
    {
        List<string> arguments = ["status", "/format:xml", "/noprompt", localPath];
        TfCommandResult result = await RunQueryAsync(GetParentDirectory(localPath), arguments, cancellationToken).ConfigureAwait(false);
        return TfvcOutputParser.ParsePendingChanges(result.StandardOutput);
    }

    /// <inheritdoc />
    public Task<TfCommandResult> CheckOutAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, bool recursive, CancellationToken cancellationToken)
    {
        List<string> arguments = ["checkout", "/noprompt"];
        if (recursive)
        {
            arguments.Add("/recursive");
        }

        arguments.AddRange(localPaths);
        return RunChangeAsync(workspace, arguments, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TfCommandResult> AddAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        List<string> arguments = ["add", "/noprompt"];
        arguments.AddRange(localPaths);
        return RunChangeAsync(workspace, arguments, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TfCommandResult> AddFoldersAsync(TfvcWorkspace workspace, IReadOnlyList<string> localFolders, CancellationToken cancellationToken)
    {
        List<string> arguments = ["add", "/recursive", "/noprompt"];
        arguments.AddRange(localFolders);
        return RunChangeAsync(workspace, arguments, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TfCommandResult> DeleteAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        List<string> arguments = ["delete", "/noprompt"];
        arguments.AddRange(localPaths);
        return RunChangeAsync(workspace, arguments, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> PreviewLocalChangesAsync(
        TfvcWorkspace workspace,
        string localFolder,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["reconcile", "/promote", "/adds", "/deletes", "/preview", "/recursive", "/noprompt", localFolder];
        TfCommandResult result = await _runner.RunAsync(localFolder, arguments, ChangeTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new TfCommandException(arguments[0], result);
        }

        return TfvcReconcileOutputParser.ParseCandidatePaths(result.StandardOutput, localFolder);
    }

    /// <inheritdoc />
    public Task<TfCommandResult> UndoAsync(TfvcWorkspace workspace, IReadOnlyList<string> itemSpecs, bool recursive, CancellationToken cancellationToken)
    {
        List<string> arguments = ["undo", "/workspace:" + workspace.Name, "/noprompt"];
        if (recursive)
        {
            arguments.Add("/recursive");
        }

        arguments.AddRange(itemSpecs);
        return RunChangeAsync(workspace, arguments, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TfCommandResult> CheckInAsync(TfvcWorkspace workspace, string comment, IReadOnlyList<string> itemSpecs, CancellationToken cancellationToken)
    {
        return RunWithCommentFileAsync(comment, commentArgument =>
        {
            List<string> arguments = ["checkin", commentArgument, "/noprompt"];
            arguments.AddRange(itemSpecs);
            return RunChangeAsync(workspace, arguments, cancellationToken);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TfCommandResult> ShelveAsync(
        TfvcWorkspace workspace,
        string shelvesetName,
        string? comment,
        IReadOnlyList<string> itemSpecs,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["shelve", shelvesetName, "/noprompt"];
        if (string.IsNullOrWhiteSpace(comment))
        {
            arguments.AddRange(itemSpecs);
            return RunChangeAsync(workspace, arguments, cancellationToken);
        }

        return RunWithCommentFileAsync(comment, commentArgument =>
        {
            arguments.Add(commentArgument);
            arguments.AddRange(itemSpecs);
            return RunChangeAsync(workspace, arguments, cancellationToken);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TfCommandResult> GetLatestAsync(TfvcWorkspace workspace, string itemSpec, bool recursive, CancellationToken cancellationToken)
    {
        List<string> arguments = ["get", "/noprompt"];
        if (recursive)
        {
            arguments.Add("/recursive");
        }

        arguments.Add(itemSpec);
        return RunChangeAsync(workspace, arguments, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TfCommandResult> DifferenceAsync(TfvcWorkspace workspace, string localPath, CancellationToken cancellationToken)
    {
        List<string> arguments = ["difference", "/format:unified", "/noprompt", localPath];
        TfCommandResult result = await _runner.RunAsync(GetWorkingDirectory(workspace), arguments, QueryTimeout, cancellationToken).ConfigureAwait(false);

        // tf difference exits with 1 when the files differ.
        if (result.ExitCode > 1)
        {
            throw new TfCommandException("difference", result);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<string?> DetectCollectionUrlAsync(string localDirectory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(localDirectory))
        {
            return null;
        }

        List<string> arguments = ["workfold", "/noprompt", localDirectory];
        TfCommandResult result = await _runner.RunAsync(localDirectory, arguments, QueryTimeout, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? TfvcOutputParser.TryParseCollectionUrl(result.StandardOutput) : null;
    }

    /// <inheritdoc />
    /// <remarks>The server accepts the change only from the author or from users with the permission to revise other users' changes.</remarks>
    public Task<TfCommandResult> UpdateChangesetCommentAsync(string collectionUrl, int changesetId, string comment, CancellationToken cancellationToken)
    {
        return RunWithCommentFileAsync(comment, async commentArgument =>
        {
            List<string> arguments =
            [
                "changeset",
                commentArgument,
                "/collection:" + collectionUrl,
                "/noprompt",
                changesetId.ToString(CultureInfo.InvariantCulture)
            ];
            TfCommandResult result = await _runner.RunAsync(GetNeutralDirectory(), arguments, ChangeTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new TfCommandException(arguments[0], result);
            }

            return result;
        }, cancellationToken);
    }

    // tf.exe reads a "/comment:" value that starts with "@" from the named file, so a comment such as "@Max: ..." cannot
    // be passed inline. Passing every comment through a file also keeps line breaks and long comments off the command line.
    private static async Task<TfCommandResult> RunWithCommentFileAsync(
        string comment,
        Func<string, Task<TfCommandResult>> run,
        CancellationToken cancellationToken)
    {
        string commentFile = Path.Combine(Path.GetTempPath(), "xve-tf-comment-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(commentFile, comment, CommentFileEncoding, cancellationToken).ConfigureAwait(false);
            return await run("/comment:@" + commentFile).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(commentFile);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<TfCommandResult> RunQueryAsync(string workingDirectory, List<string> arguments, CancellationToken cancellationToken)
    {
        TfCommandResult result = await _runner.RunAsync(workingDirectory, arguments, QueryTimeout, cancellationToken).ConfigureAwait(false);

        // tf status exits with 1 when it reports warnings next to a valid result.
        if (!result.Succeeded && string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new TfCommandException(arguments[0], result);
        }

        return result;
    }

    private async Task<TfCommandResult> RunChangeAsync(TfvcWorkspace workspace, List<string> arguments, CancellationToken cancellationToken)
    {
        if (TfCommandRunner.EstimateCommandLineLength(arguments) > TfCommandRunner.MAX_COMMAND_LINE_LENGTH)
        {
            throw new TfCommandLineTooLongException();
        }

        TfCommandResult result = await _runner.RunAsync(GetWorkingDirectory(workspace), arguments, ChangeTimeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new TfCommandException(arguments[0], result);
        }

        return result;
    }

    // tf.exe resolves the workspace of commands without /workspace from the working directory.
    private static string GetWorkingDirectory(TfvcWorkspace workspace)
    {
        return workspace.GetWorkingDirectory() ?? GetNeutralDirectory();
    }

    private static string GetParentDirectory(string localPath)
    {
        string? directory = Directory.Exists(localPath) ? localPath : Path.GetDirectoryName(localPath);
        return string.IsNullOrWhiteSpace(directory) ? GetNeutralDirectory() : directory;
    }

    private static string GetNeutralDirectory()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
