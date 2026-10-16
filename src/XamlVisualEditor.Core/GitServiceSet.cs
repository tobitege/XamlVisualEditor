using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.Core;

/// <summary>
/// The git services a consumer can use. <see cref="Service"/> is always present; every further capability is null
/// when the git implementation does not provide it.
/// </summary>
public sealed class GitServiceSet
{
    /// <summary>Creates a set with the basic status, diff, stage and commit service.</summary>
    public GitServiceSet(IGitService service)
    {
        Service = service;
    }

    /// <summary>Gets the status, diff, stage and commit service.</summary>
    public IGitService Service { get; }

    /// <summary>Gets the git directory, remote and init service, or null.</summary>
    public IGitRepositoryService? Repository { get; init; }

    /// <summary>Gets the report of the git commands that ran, or null.</summary>
    public IGitCommandLog? CommandLog { get; init; }

    /// <summary>Gets the branch list, switch, create and delete service, or null.</summary>
    public IGitBranchService? Branches { get; init; }

    /// <summary>Gets the remote list, fetch, pull and push service, or null.</summary>
    public IGitRemoteService? Remotes { get; init; }

    /// <summary>
    /// Creates a set whose optional capabilities are the interfaces <paramref name="service"/> implements.
    /// </summary>
    public static GitServiceSet FromService(IGitService service)
    {
        return new GitServiceSet(service)
        {
            Repository = service as IGitRepositoryService,
            CommandLog = service as IGitCommandLog,
            Branches = service as IGitBranchService,
            Remotes = service as IGitRemoteService
        };
    }
}
