using XamlVisualEditor.Core;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>The part of a repository a <see cref="GitRepositoryWatcher"/> watches.</summary>
public enum GitWatchedArea
{
    /// <summary>The git directory with HEAD, index and the merge and rebase state.</summary>
    GitDirectory,

    /// <summary>The common git directory of a linked worktree with the branches, packed-refs and config.</summary>
    CommonDirectory,

    /// <summary>The working folder, recursively.</summary>
    WorkingTree
}

/// <summary>Reports that watching an area failed and stopped.</summary>
public sealed class GitWatcherFailedEventArgs : EventArgs
{
    /// <summary>Creates the event args.</summary>
    public GitWatcherFailedEventArgs(GitWatchedArea area, string path, Exception exception)
    {
        Area = area;
        Path = path;
        Exception = exception;
    }

    /// <summary>Gets the area that is no longer watched.</summary>
    public GitWatchedArea Area { get; }

    /// <summary>Gets the watched folder.</summary>
    public string Path { get; }

    /// <summary>Gets the reason.</summary>
    public Exception Exception { get; }
}

/// <summary>
/// Watches the state files of a repository and, optionally, its working folder, and reports changes that can change
/// the git status. In the git directories only the listed state files count, so files git writes while it reads the
/// status, such as the cookies of the fsmonitor daemon, cause no refresh. In the working folder, the git directory
/// and build output folders are ignored.
/// </summary>
public sealed class GitRepositoryWatcher : IDisposable
{
    /// <summary>The files of a git directory that hold the state of its working tree.</summary>
    private static readonly string[] WorkingTreeStateFiles =
        ["HEAD", "index", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "FETCH_HEAD"];

    /// <summary>The folders of a git directory whose files hold the state of its working tree.</summary>
    private static readonly string[] WorkingTreeStateFolders = ["refs", "rebase-merge", "rebase-apply"];

    /// <summary>The files of the common git directory that every worktree shares.</summary>
    private static readonly string[] SharedStateFiles = ["packed-refs", "config"];

    /// <summary>The folders of the common git directory that every worktree shares.</summary>
    private static readonly string[] SharedStateFolders = ["refs"];

    /// <summary>The file of a git directory that holds packed references.</summary>
    private static readonly string[] RefsFiles = ["packed-refs"];

    /// <summary>The folder of a git directory that holds loose references.</summary>
    private static readonly string[] RefsFolders = ["refs"];

    private static readonly string[] IgnoredWorkingTreeFolders = [".git", "bin", "obj", ".vs", "artifacts", "TestResults"];

    private readonly object _sync = new();
    private readonly Dictionary<GitWatchedArea, FileSystemWatcher> _watchers = new();
    private bool _disposed;

    /// <summary>Creates a watcher; nothing is watched before <see cref="Start"/>.</summary>
    /// <param name="repositoryRoot">The working folder root.</param>
    /// <param name="gitDirectory">The absolute git directory, or null when it is unknown.</param>
    /// <param name="commonDirectory">
    /// The absolute common git directory; null or the git directory itself unless the repository is a linked worktree.
    /// </param>
    /// <param name="watchWorkingTree">Whether the working folder is watched as well.</param>
    public GitRepositoryWatcher(string repositoryRoot, string? gitDirectory, string? commonDirectory, bool watchWorkingTree)
    {
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        GitDirectory = gitDirectory is null ? null : Path.GetFullPath(gitDirectory);
        CommonDirectory = commonDirectory is null ? GitDirectory : Path.GetFullPath(commonDirectory);
        WatchWorkingTree = watchWorkingTree;
    }

    /// <summary>Raised on a thread-pool thread for every relevant file system change.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised on a thread-pool thread before <see cref="Changed"/> when the change is below <c>refs</c> or of
    /// <c>packed-refs</c> in a watched git directory, or when changes were lost.
    /// </summary>
    public event EventHandler? RefsChanged;

    /// <summary>Raised when watching an area failed; that area is not watched afterwards.</summary>
    public event EventHandler<GitWatcherFailedEventArgs>? Failed;

    /// <summary>Gets the working folder root.</summary>
    public string RepositoryRoot { get; }

    /// <summary>Gets the git directory, or null.</summary>
    public string? GitDirectory { get; }

    /// <summary>Gets the common git directory; equal to <see cref="GitDirectory"/> outside linked worktrees.</summary>
    public string? CommonDirectory { get; }

    /// <summary>Gets whether the working folder was requested to be watched.</summary>
    public bool WatchWorkingTree { get; }

    /// <summary>Gets whether the git directory is watched.</summary>
    public bool IsWatchingGitDirectory => IsWatching(GitWatchedArea.GitDirectory);

    /// <summary>Gets whether a separate common git directory is watched.</summary>
    public bool IsWatchingCommonDirectory => IsWatching(GitWatchedArea.CommonDirectory);

    /// <summary>Gets whether the working folder is watched.</summary>
    public bool IsWatchingWorkingTree => IsWatching(GitWatchedArea.WorkingTree);

    private bool SharesCommonDirectory => FileSystemPathComparison.Equals(GitDirectory, CommonDirectory);

    /// <summary>
    /// Starts watching. An area that cannot be watched raises <see cref="Failed"/> before the method returns.
    /// </summary>
    public void Start()
    {
        if (GitDirectory is not null)
        {
            StartArea(GitWatchedArea.GitDirectory, GitDirectory);
        }

        if (CommonDirectory is not null && !SharesCommonDirectory)
        {
            StartArea(GitWatchedArea.CommonDirectory, CommonDirectory);
        }

        if (WatchWorkingTree)
        {
            StartArea(GitWatchedArea.WorkingTree, RepositoryRoot);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FileSystemWatcher[] watchers;
        lock (_sync)
        {
            _disposed = true;
            watchers = _watchers.Values.ToArray();
            _watchers.Clear();
        }

        foreach (FileSystemWatcher watcher in watchers)
        {
            watcher.Dispose();
        }
    }

    /// <summary>
    /// Returns whether a change below a git directory is one of the state files of its working tree: HEAD, index,
    /// the merge, cherry-pick, revert and fetch heads, the rebase folders and the refs of the worktree.
    /// </summary>
    internal static bool IsRelevantGitDirectoryPath(string gitDirectory, string fullPath)
    {
        return IsStatePath(gitDirectory, fullPath, WorkingTreeStateFiles, WorkingTreeStateFolders);
    }

    /// <summary>Returns whether a change below a common git directory is in its refs, packed-refs or config.</summary>
    internal static bool IsRelevantCommonDirectoryPath(string commonDirectory, string fullPath)
    {
        return IsStatePath(commonDirectory, fullPath, SharedStateFiles, SharedStateFolders);
    }

    /// <summary>Returns whether a change below a git directory is below its <c>refs</c> folder or of its <c>packed-refs</c>.</summary>
    internal static bool IsRefsPath(string gitDirectory, string fullPath)
    {
        return IsStatePath(gitDirectory, fullPath, RefsFiles, RefsFolders);
    }

    /// <summary>
    /// Returns whether a change below the working folder can change the git status. The <c>.git</c> folder is
    /// left to the git directory watcher.
    /// </summary>
    internal static bool IsRelevantWorkingTreePath(string repositoryRoot, string? gitDirectory, string fullPath)
    {
        if (gitDirectory is not null && FileSystemPathComparison.IsSameOrDescendant(fullPath, gitDirectory))
        {
            return false;
        }

        string[]? segments = GetRelativeSegments(repositoryRoot, fullPath);
        if (segments is null)
        {
            return false;
        }

        foreach (string segment in segments)
        {
            foreach (string ignored in IgnoredWorkingTreeFolders)
            {
                if (string.Equals(segment, ignored, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsStatePath(string directory, string fullPath, string[] files, string[] folders)
    {
        // Git writes a lock file next to a state file and renames it over the file; the rename reports the file.
        if (fullPath.EndsWith(".lock", StringComparison.Ordinal))
        {
            return false;
        }

        string[]? segments = GetRelativeSegments(directory, fullPath);
        if (segments is null)
        {
            return false;
        }

        string first = segments[0];
        return (segments.Length == 1 && Contains(files, first)) || Contains(folders, first);
    }

    private static bool Contains(string[] names, string name)
    {
        foreach (string candidate in names)
        {
            if (string.Equals(candidate, name, FileSystemPathComparison.Comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the segments of the path relative to the folder; null for the folder itself or a path outside it.</summary>
    private static string[]? GetRelativeSegments(string folder, string fullPath)
    {
        string relative = Path.GetRelativePath(folder, fullPath);
        if (relative == "."
            || Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        return relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
    }

    private bool IsWatching(GitWatchedArea area)
    {
        lock (_sync)
        {
            return _watchers.ContainsKey(area);
        }
    }

    private void StartArea(GitWatchedArea area, string path)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            FileSystemEventHandler changed = (_, args) => OnChanged(area, args.FullPath);
            RenamedEventHandler renamed = (_, args) => OnChanged(area, args.FullPath);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += renamed;
            watcher.Error += (_, args) => OnError(area, path, args.GetException());

            lock (_sync)
            {
                if (_disposed)
                {
                    watcher.Dispose();
                    return;
                }

                _watchers[area] = watcher;
            }

            watcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException
                                              or PlatformNotSupportedException)
        {
            StopArea(area, watcher);
            Failed?.Invoke(this, new GitWatcherFailedEventArgs(area, path, exception));
        }
    }

    private void OnChanged(GitWatchedArea area, string fullPath)
    {
        bool relevant = area switch
        {
            GitWatchedArea.GitDirectory => IsRelevantGitDirectoryPath(GitDirectory!, fullPath)
                || (SharesCommonDirectory && IsRelevantCommonDirectoryPath(GitDirectory!, fullPath)),
            GitWatchedArea.CommonDirectory => IsRelevantCommonDirectoryPath(CommonDirectory!, fullPath),
            _ => IsRelevantWorkingTreePath(RepositoryRoot, GitDirectory, fullPath)
        };
        bool refs = area switch
        {
            GitWatchedArea.GitDirectory => IsRefsPath(GitDirectory!, fullPath),
            GitWatchedArea.CommonDirectory => IsRefsPath(CommonDirectory!, fullPath),
            _ => false
        };
        if (refs)
        {
            RefsChanged?.Invoke(this, EventArgs.Empty);
        }

        if (relevant)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnError(GitWatchedArea area, string path, Exception exception)
    {
        // A full event buffer loses single events only; a refresh reads the whole status anyway.
        if (exception is InternalBufferOverflowException)
        {
            if (area != GitWatchedArea.WorkingTree)
            {
                RefsChanged?.Invoke(this, EventArgs.Empty);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        FileSystemWatcher? watcher;
        lock (_sync)
        {
            _watchers.TryGetValue(area, out watcher);
        }

        if (watcher is null)
        {
            return;
        }

        StopArea(area, watcher);
        Failed?.Invoke(this, new GitWatcherFailedEventArgs(area, path, exception));
    }

    private void StopArea(GitWatchedArea area, FileSystemWatcher? watcher)
    {
        lock (_sync)
        {
            if (_watchers.TryGetValue(area, out FileSystemWatcher? current) && ReferenceEquals(current, watcher))
            {
                _watchers.Remove(area);
            }
        }

        if (watcher is not null)
        {
            // The watcher may be raising this error; disposing it on another thread lets the handler return first.
            ThreadPool.QueueUserWorkItem(static state => ((FileSystemWatcher)state!).Dispose(), watcher);
        }
    }
}
