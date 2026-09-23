using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>Checks out local files for edit.</summary>
    Task<TfCommandResult> CheckOutAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, CancellationToken cancellationToken);

    /// <summary>Adds local files to version control.</summary>
    Task<TfCommandResult> AddAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, CancellationToken cancellationToken);

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
    public Task<TfCommandResult> CheckOutAsync(TfvcWorkspace workspace, IReadOnlyList<string> localPaths, CancellationToken cancellationToken)
    {
        List<string> arguments = ["checkout", "/noprompt"];
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
        List<string> arguments = ["checkin", "/comment:" + comment, "/noprompt"];
        arguments.AddRange(itemSpecs);
        return RunChangeAsync(workspace, arguments, cancellationToken);
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
        if (!string.IsNullOrWhiteSpace(comment))
        {
            arguments.Add("/comment:" + comment);
        }

        arguments.AddRange(itemSpecs);
        return RunChangeAsync(workspace, arguments, cancellationToken);
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
