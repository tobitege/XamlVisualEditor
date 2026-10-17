using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>
/// Shows the Git status in the explorers: a changed file gets the colour and letter of its change, every folder above
/// a change the colour Modified, or Conflicted when a conflict lies below it. The decorations follow the snapshots of
/// the session and the switch, which is saved in the extension storage.
/// </summary>
public sealed class GitExplorerDecorations : IDisposable
{
    private readonly object _gate = new();
    private readonly GitSession _session;
    private readonly IExplorerDecorationLayer? _layer;
    private readonly GitSettingsStore? _settings;
    private readonly IContextKeys? _contextKeys;
    private readonly IExtensionLogger? _logger;
    private readonly IDisposable _subscription;
    private bool _enabled = true;
    private bool _disposed;

    /// <summary>Creates the decorations, switched on until <see cref="LoadAsync"/> reads the saved switch.</summary>
    public GitExplorerDecorations(
        GitSession session,
        IExplorerDecorations? decorations,
        GitSettingsStore? settings = null,
        IContextKeys? contextKeys = null,
        IExtensionLogger? logger = null)
    {
        _session = session;
        _layer = decorations?.CreateLayer();
        _settings = settings;
        _contextKeys = contextKeys;
        _logger = logger;
        _contextKeys?.Set(GitSettingsStore.EXPLORER_DECORATIONS_KEY, true);
        _subscription = session.Snapshots.Subscribe(Apply);
    }

    /// <summary>Gets whether the explorers show the Git status.</summary>
    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    /// <summary>Reads the saved switch.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            SetEnabled(await _settings.LoadExplorerDecorationsAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.Error(GitStrings.LogSettingsFailed, exception);
        }
    }

    /// <summary>Switches the Git status in the explorers on or off and saves the switch.</summary>
    public async Task ToggleAsync()
    {
        bool enabled = !IsEnabled;
        SetEnabled(enabled);
        if (_settings is null)
        {
            return;
        }

        try
        {
            await _settings.SaveExplorerDecorationsAsync(enabled, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.Error(GitStrings.LogSettingsFailed, exception);
        }
    }

    /// <summary>
    /// Builds the decorations of a status: one entry per change and one entry without badge per folder from the
    /// folder of the change up to the repository root.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, ExplorerDecoration>> Build(
        string repositoryRoot,
        IReadOnlyList<GitFileChange> changes)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        Dictionary<string, ExplorerDecoration> entries = new(FileSystemPathComparison.Comparer);
        // Every folder maps to whether a conflict lies below it; its ancestors are marked at least as strongly.
        Dictionary<string, bool> folders = new(FileSystemPathComparison.Comparer);
        foreach (GitFileChange change in changes)
        {
            string path = Path.Join(root, change.Path.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            ExplorerDecoration decoration = Decorate(change);
            entries[path] = decoration;
            bool conflicted = decoration.Kind == ExplorerDecorationKind.Conflicted;
            for (string? folder = Path.GetDirectoryName(path);
                 folder is not null && FileSystemPathComparison.IsSameOrDescendant(folder, root);
                 folder = Path.GetDirectoryName(folder))
            {
                if (folders.TryGetValue(folder, out bool known) && (known || !conflicted))
                {
                    break;
                }

                folders[folder] = conflicted;
            }
        }

        foreach ((string folder, bool conflicted) in folders)
        {
            entries.TryAdd(folder, new ExplorerDecoration(
                conflicted ? ExplorerDecorationKind.Conflicted : ExplorerDecorationKind.Modified));
        }

        return entries.ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _subscription.Dispose();
        _layer?.Dispose();
    }

    private static ExplorerDecoration Decorate(GitFileChange change) => change switch
    {
        { IsConflicted: true } => new(ExplorerDecorationKind.Conflicted, "!", GitStrings.ChangeConflicted),
        { IsUntracked: true } => new(ExplorerDecorationKind.Added, "U", GitStrings.ChangeUntracked),
        { IndexStatus: GitChangeKind.Deleted } or { WorkTreeStatus: GitChangeKind.Deleted } =>
            new(ExplorerDecorationKind.Deleted, "D", GitStrings.ChangeDeleted),
        { IndexStatus: GitChangeKind.Added } => new(ExplorerDecorationKind.Added, "A", GitStrings.ChangeAdded),
        { IsRenamed: true } => new(ExplorerDecorationKind.Added, "R", GitStrings.ChangeRenamed),
        _ => new(ExplorerDecorationKind.Modified, "M", GitStrings.ChangeModified)
    };

    private void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
        }

        _contextKeys?.Set(GitSettingsStore.EXPLORER_DECORATIONS_KEY, enabled);
        Apply(_session.Current);
    }

    // Snapshots arrive on the thread pool and the switch on the UI thread; the lock keeps the last Set in step with
    // the switch.
    private void Apply(GitSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || _layer is null)
            {
                return;
            }

            _layer.Set(_enabled && snapshot.State == GitSessionState.Repository && snapshot.RepositoryRoot is { } root
                ? Build(root, snapshot.Changes)
                : []);
        }
    }
}
