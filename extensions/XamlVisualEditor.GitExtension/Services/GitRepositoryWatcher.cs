using XamlVisualEditor.Core;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>The part of a repository a <see cref="GitRepositoryWatcher"/> watches.</summary>
public enum GitWatchedArea
{
    /// <summary>The git directory with HEAD, index, refs and the merge state files.</summary>
    GitDirectory,

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
/// Watches the git directory of a repository and, optionally, its working folder, and reports changes that can
/// change the git status. Lock files, objects, logs and build output folders are ignored.
/// </summary>
public sealed class GitRepositoryWatcher : IDisposable
{
    private static readonly string[] IgnoredGitDirectoryFolders = ["objects", "logs"];
    private static readonly string[] IgnoredWorkingTreeFolders = [".git", "bin", "obj", ".vs", "artifacts", "TestResults"];

    private readonly object _sync = new();
    private FileSystemWatcher? _gitDirectoryWatcher;
    private FileSystemWatcher? _workingTreeWatcher;
    private bool _disposed;

    /// <summary>Creates a watcher; nothing is watched before <see cref="Start"/>.</summary>
    /// <param name="repositoryRoot">The working folder root.</param>
    /// <param name="gitDirectory">The absolute git directory, or null when it is unknown.</param>
    /// <param name="watchWorkingTree">Whether the working folder is watched as well.</param>
    public GitRepositoryWatcher(string repositoryRoot, string? gitDirectory, bool watchWorkingTree)
    {
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        GitDirectory = gitDirectory is null ? null : Path.GetFullPath(gitDirectory);
        WatchWorkingTree = watchWorkingTree;
    }

    /// <summary>Raised on a thread-pool thread for every relevant file system change.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when watching an area failed; that area is not watched afterwards.</summary>
    public event EventHandler<GitWatcherFailedEventArgs>? Failed;

    /// <summary>Gets the working folder root.</summary>
    public string RepositoryRoot { get; }

    /// <summary>Gets the git directory, or null.</summary>
    public string? GitDirectory { get; }

    /// <summary>Gets whether the working folder was requested to be watched.</summary>
    public bool WatchWorkingTree { get; }

    /// <summary>Gets whether the git directory is watched.</summary>
    public bool IsWatchingGitDirectory
    {
        get
        {
            lock (_sync)
            {
                return _gitDirectoryWatcher is not null;
            }
        }
    }

    /// <summary>Gets whether the working folder is watched.</summary>
    public bool IsWatchingWorkingTree
    {
        get
        {
            lock (_sync)
            {
                return _workingTreeWatcher is not null;
            }
        }
    }

    /// <summary>
    /// Starts watching. An area that cannot be watched raises <see cref="Failed"/> before the method returns.
    /// </summary>
    public void Start()
    {
        if (GitDirectory is not null)
        {
            StartArea(GitWatchedArea.GitDirectory, GitDirectory);
        }

        if (WatchWorkingTree)
        {
            StartArea(GitWatchedArea.WorkingTree, RepositoryRoot);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FileSystemWatcher? gitDirectoryWatcher;
        FileSystemWatcher? workingTreeWatcher;
        lock (_sync)
        {
            _disposed = true;
            gitDirectoryWatcher = _gitDirectoryWatcher;
            workingTreeWatcher = _workingTreeWatcher;
            _gitDirectoryWatcher = null;
            _workingTreeWatcher = null;
        }

        gitDirectoryWatcher?.Dispose();
        workingTreeWatcher?.Dispose();
    }

    /// <summary>Returns whether a change below the git directory can change the git status.</summary>
    internal static bool IsRelevantGitDirectoryPath(string gitDirectory, string fullPath)
    {
        if (fullPath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relative = Path.GetRelativePath(gitDirectory, fullPath);
        if (relative == "." || IsOutside(relative))
        {
            return true;
        }

        string firstSegment = FirstSegment(relative);
        foreach (string ignored in IgnoredGitDirectoryFolders)
        {
            if (string.Equals(firstSegment, ignored, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
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

        string relative = Path.GetRelativePath(repositoryRoot, fullPath);
        if (relative == "." || IsOutside(relative))
        {
            return false;
        }

        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
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

                if (area == GitWatchedArea.GitDirectory)
                {
                    _gitDirectoryWatcher = watcher;
                }
                else
                {
                    _workingTreeWatcher = watcher;
                }
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
        bool relevant = area == GitWatchedArea.GitDirectory
            ? IsRelevantGitDirectoryPath(GitDirectory!, fullPath)
            : IsRelevantWorkingTreePath(RepositoryRoot, GitDirectory, fullPath);
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
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        FileSystemWatcher? watcher;
        lock (_sync)
        {
            watcher = area == GitWatchedArea.GitDirectory ? _gitDirectoryWatcher : _workingTreeWatcher;
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
            if (area == GitWatchedArea.GitDirectory && ReferenceEquals(_gitDirectoryWatcher, watcher))
            {
                _gitDirectoryWatcher = null;
            }
            else if (area == GitWatchedArea.WorkingTree && ReferenceEquals(_workingTreeWatcher, watcher))
            {
                _workingTreeWatcher = null;
            }
        }

        if (watcher is not null)
        {
            // The watcher may be raising this error; disposing it on another thread lets the handler return first.
            ThreadPool.QueueUserWorkItem(static state => ((FileSystemWatcher)state!).Dispose(), watcher);
        }
    }

    private static bool IsOutside(string relativePath)
    {
        return Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string FirstSegment(string relativePath)
    {
        int separator = relativePath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator < 0 ? relativePath : relativePath[..separator];
    }
}
