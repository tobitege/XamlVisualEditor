using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.ToolboxExtension;

public sealed partial class ToolboxPanelViewModel
{
    private const string FavoriteAssembliesKey = "toolbox.assemblyFavorites";
    private readonly IWorkspaceInfo? _workspaceInfo;
    private readonly IExtensionStorage? _preferenceStorage;
    private readonly HashSet<string> _hiddenAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private string _visibilityKey = "toolbox.assemblyVisibility:no-workspace";
    private string _collapseKey = "toolbox.assemblyCollapsed:no-workspace";
    // Errors of a whole load; the lists themselves report their errors under their storage keys.
    private const string PreferenceLoadErrorKey = "";
    // A stored list that cannot be read is not overwritten, so a later edit cannot replace what is still on disk.
    private readonly HashSet<string> _unreadablePreferenceKeys = new(StringComparer.Ordinal);
    // Each error stays until its key is written or read successfully.
    private readonly Dictionary<string, string> _preferenceErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _preferenceSaveVersions = new(StringComparer.Ordinal);
    private bool _preferencesReady = true;
    private int _preferenceLoadVersion;
    private Task _preferenceWrites = Task.CompletedTask;
    private Task _preferenceSaves = Task.CompletedTask;
    private Task _preferenceLoad = Task.CompletedTask;

    /// <summary>Gets the preference writes that have not reached the storage yet.</summary>
    internal Task PendingPreferenceWrites => _preferenceWrites;

    /// <summary>Gets the saves that have not reported their result yet.</summary>
    internal Task PendingPreferenceSaves => _preferenceSaves;

    /// <summary>Gets the most recently started preference load.</summary>
    internal Task PendingPreferenceLoad => Volatile.Read(ref _preferenceLoad);

    /// <summary>Gets or sets how long disposing waits for the pending preference writes.</summary>
    internal TimeSpan PreferenceFlushTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public ObservableCollection<ToolboxAssemblyGroup> AssemblyChoices { get; } = new();

    /// <summary>Gets whether libraries can be shown or hidden: the stored list is readable and no catalog load runs.</summary>
    public bool CanEditAssemblyVisibility => CanEditPreference(_visibilityKey) && !IsLoading;

    /// <summary>Gets whether libraries can be expanded or collapsed.</summary>
    public bool CanEditCollapsedAssemblies => CanEditPreference(_collapseKey);

    /// <summary>Gets whether libraries can be added to or removed from the favorites.</summary>
    public bool CanEditFavoriteAssemblies => CanEditPreference(FavoriteAssembliesKey);

    public string AssemblyFilterSummary => string.Format(UiStrings.ToolboxAssembliesEnabled,
        AssemblyChoices.Count(group => group.IsIncluded), AssemblyChoices.Count);

    /// <summary>Gets the preference errors that are not resolved yet.</summary>
    public string? PreferenceError => _preferenceErrors.Count == 0 ? null
        : string.Join(Environment.NewLine, _preferenceErrors.Values);

    public ReactiveCommand<RxVoid, RxVoid> ShowAllAssembliesCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> HideAllAssembliesCommand { get; }

    public Task InitializePreferencesAsync(CancellationToken cancellationToken = default)
    {
        Task load = LoadAssemblyPreferencesAsync(_workspaceInfo?.WorkspacePath, cancellationToken);
        Volatile.Write(ref _preferenceLoad, load);
        return load;
    }

    // The workspace can change on any thread; the load starts on the UI thread, where the lists are edited.
    private void OnPreferencesWorkspaceChanged(object? sender, WorkspaceChangedEventArgs args)
    {
        TaskCompletionSource<Task> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiScheduler.Schedule(() => started.SetResult(LoadAssemblyPreferencesAsync(args.WorkspacePath, CancellationToken.None)));
        Volatile.Write(ref _preferenceLoad, started.Task.Unwrap());
    }

    private bool CanEditPreference(string key) =>
        _preferencesReady && !_disposed && !_unreadablePreferenceKeys.Contains(key);

    private async Task LoadAssemblyPreferencesAsync(string? workspacePath, CancellationToken cancellationToken)
    {
        if (_disposed) return;
        int version = Interlocked.Increment(ref _preferenceLoadVersion);
        _preferencesReady = false;
        NotifyPreferenceAvailability();
        Task pendingWrites = _preferenceWrites;
        try
        {
            // Lists written just before a workspace change are read back only after they reached the storage; a failed
            // write reports its own error.
            try { await pendingWrites.ConfigureAwait(false); }
            catch (Exception) { }
            string identity = string.IsNullOrWhiteSpace(workspacePath)
                ? "no-workspace" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
            if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
            string visibilityKey = "toolbox.assemblyVisibility:" + identity;
            string collapseKey = "toolbox.assemblyCollapsed:" + identity;
            await UpdateUiAsync(() =>
            {
                if (!IsCurrentPreferenceLoad(version)) return;
                _collapseKey = collapseKey;
                if (_visibilityKey != visibilityKey)
                {
                    _visibilityKey = visibilityKey;
                    _hiddenAssemblies.Clear();
                    _collapsedAssemblies.Clear();
                    _assemblyGroups.Clear();
                    _updatingFilters = true;
                    try
                    {
                        SearchText = string.Empty;
                        SelectedLibrary = _allLibraries;
                        SelectedCategory = _allCategories;
                    }
                    finally { _updatingFilters = false; }
                    ApplyFilter();
                }
                NotifyPreferenceAvailability();
            }).ConfigureAwait(false);
            if (!IsCurrentPreferenceLoad(version)) return;

            StoredPreference hidden = await TryReadPreferenceAsync(visibilityKey, cancellationToken).ConfigureAwait(false);
            StoredPreference collapsed = await TryReadPreferenceAsync(collapseKey, cancellationToken).ConfigureAwait(false);
            StoredPreference favorites = await TryReadPreferenceAsync(FavoriteAssembliesKey, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await UpdateUiAsync(() =>
            {
                if (!IsCurrentPreferenceLoad(version)) return;
                _unreadablePreferenceKeys.Clear();
                ApplyStoredPreference(_hiddenAssemblies, hidden, visibilityKey);
                ApplyStoredPreference(_collapsedAssemblies, collapsed, collapseKey);
                ApplyStoredPreference(_favoriteAssemblies, favorites, FavoriteAssembliesKey);
                SetPreferenceError(PreferenceLoadErrorKey, null);
                _preferencesReady = true;
                ApplyFilter();
                NotifyPreferenceAvailability();
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await UpdateUiAsync(() =>
            {
                if (!IsCurrentPreferenceLoad(version)) return;
                // Without the stored lists an edit would replace them, so editing stays off until the next load.
                _preferencesReady = false;
                SetPreferenceError(PreferenceLoadErrorKey, string.Format(UiStrings.ToolboxPreferencesLoadFailed, error.Message));
                NotifyPreferenceAvailability();
            }).ConfigureAwait(false);
        }
    }

    /// <summary>Holds a stored list, or null with the reason it could not be read.</summary>
    private readonly record struct StoredPreference(string[]? Values, string? Error);

    private async Task<StoredPreference> TryReadPreferenceAsync(string key, CancellationToken cancellationToken)
    {
        if (_preferenceStorage is null) return new StoredPreference(Array.Empty<string>(), null);
        try
        {
            string[]? values = await _preferenceStorage.GetAsync<string[]>(key, cancellationToken).ConfigureAwait(false);
            return new StoredPreference(values ?? Array.Empty<string>(), null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new StoredPreference(null, error.Message);
        }
    }

    private void ApplyStoredPreference(HashSet<string> current, StoredPreference stored, string key)
    {
        if (stored.Values is null)
        {
            _unreadablePreferenceKeys.Add(key);
            SetPreferenceError(key, string.Format(UiStrings.ToolboxPreferencesLoadFailed, stored.Error));
            return;
        }

        SetPreferenceError(key, null);
        current.Clear();
        current.UnionWith(stored.Values.Where(name => name is not null));
    }

    private void SetPreferenceError(string key, string? message)
    {
        bool changed;
        if (message is null)
        {
            changed = _preferenceErrors.Remove(key);
        }
        else
        {
            changed = !_preferenceErrors.TryGetValue(key, out string? current) || current != message;
            _preferenceErrors[key] = message;
        }
        if (changed) this.RaisePropertyChanged(nameof(PreferenceError));
    }

    private bool IsCurrentPreferenceLoad(int version) => !_disposed && version == Volatile.Read(ref _preferenceLoadVersion);

    internal bool IsAssemblyIncluded(string name) => !_hiddenAssemblies.Contains(name);
    internal bool IsAssemblyFavorite(string name) => _favoriteAssemblies.Contains(name);

    internal void SetAssemblyExpanded(ToolboxAssemblyGroup group, bool expanded)
    {
        if (!CanEditCollapsedAssemblies) return;
        group.ApplyExpandedState(expanded);
        bool changed = expanded ? _collapsedAssemblies.Remove(group.AssemblyName) : _collapsedAssemblies.Add(group.AssemblyName);
        if (changed) SaveCollapsedAssemblies();
    }

    private void SaveCollapsedAssemblies() =>
        _ = SaveAssemblyPreferencesAsync(_collapseKey, _collapsedAssemblies.OrderBy(name => name, StringComparer.Ordinal).ToArray());

    internal void SetAssemblyIncluded(ToolboxAssemblyGroup group, bool included)
    {
        if (!CanEditAssemblyVisibility) return;
        bool changed = included ? _hiddenAssemblies.Remove(group.AssemblyName) : _hiddenAssemblies.Add(group.AssemblyName);
        if (!changed) return;
        ApplyFilter();
        _ = SaveAssemblyPreferencesAsync(_visibilityKey, _hiddenAssemblies.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    internal void SetAssemblyFavorite(ToolboxAssemblyGroup group, bool favorite)
    {
        if (!CanEditFavoriteAssemblies || !group.CanFavorite) return;
        bool changed = favorite ? _favoriteAssemblies.Add(group.AssemblyName) : _favoriteAssemblies.Remove(group.AssemblyName);
        if (!changed) return;
        ApplyFilter();
        _ = SaveAssemblyPreferencesAsync(FavoriteAssembliesKey, _favoriteAssemblies.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    private void SetAllAssembliesIncluded(bool included)
    {
        if (!CanEditAssemblyVisibility) return;
        bool changed = false;
        foreach (ToolboxAssemblyGroup group in AssemblyChoices)
            changed |= included ? _hiddenAssemblies.Remove(group.AssemblyName) : _hiddenAssemblies.Add(group.AssemblyName);
        if (!changed) return;
        ApplyFilter();
        _ = SaveAssemblyPreferencesAsync(_visibilityKey, _hiddenAssemblies.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    private Task SaveAssemblyPreferencesAsync(string key, string[] values)
    {
        if (_disposed || _preferenceStorage is not { } storage || _unreadablePreferenceKeys.Contains(key)) return Task.CompletedTask;
        int version = _preferenceSaveVersions.TryGetValue(key, out int previous) ? previous + 1 : 1;
        _preferenceSaveVersions[key] = version;
        // The storage writes and flushes synchronously; the writes run in order on the thread pool and never wait for
        // the UI thread.
        Task write = _preferenceWrites.ContinueWith(_ => storage.SetAsync(key, values, CancellationToken.None),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _preferenceWrites = write;
        Task save = ReportPreferenceSaveAsync(write, key, version);
        _preferenceSaves = Task.WhenAll(_preferenceSaves, save);
        return save;
    }

    private async Task ReportPreferenceSaveAsync(Task write, string key, int version)
    {
        string? error = null;
        try { await write.ConfigureAwait(false); }
        catch (Exception failure) { error = string.Format(UiStrings.ToolboxPreferencesSaveFailed, failure.Message); }
        await UpdateUiAsync(() =>
        {
            // The latest write of a key decides whether its error shows; other keys keep theirs.
            if (!_disposed && _preferenceSaveVersions.TryGetValue(key, out int latest) && latest == version)
                SetPreferenceError(key, error);
        }).ConfigureAwait(false);
    }

    private void FlushPreferenceWrites()
    {
        // The writes never wait for the UI thread; the timeout keeps a storage that does not finish from blocking shutdown.
        try { _preferenceWrites.Wait(PreferenceFlushTimeout); }
        catch (AggregateException) { }
    }

    private void RefreshAssemblyChoices()
    {
        var assemblies = Items.GroupBy(item => item.AssemblyName, StringComparer.Ordinal).ToArray();
        HashSet<string> names = assemblies.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string obsolete in _assemblyGroups.Keys.Where(name => !names.Contains(name)).ToArray())
            _assemblyGroups.Remove(obsolete);
        foreach (var assembly in assemblies)
        {
            if (!_assemblyGroups.TryGetValue(assembly.Key, out ToolboxAssemblyGroup? group))
            {
                group = new ToolboxAssemblyGroup(this, assembly.Key);
                _assemblyGroups.Add(assembly.Key, group);
            }
            bool isStandard = assembly.All(item => item.IsStandardControl);
            group.UpdateCatalogInfo(assembly.Count(), !string.IsNullOrEmpty(assembly.Key) && !isStandard,
                isStandard, assembly.First().AssemblyVersion);
            group.ApplyExpandedState(!_collapsedAssemblies.Contains(group.AssemblyName));
        }
        SynchronizeGroups(AssemblyChoices, _assemblyGroups.Values
            .OrderByDescending(group => group.IsStandardAssembly)
            .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.AssemblyName, StringComparer.Ordinal).ToArray());
        this.RaisePropertyChanged(nameof(AssemblyFilterSummary));
    }

    private static void SynchronizeGroups(ObservableCollection<ToolboxAssemblyGroup> current, IReadOnlyList<ToolboxAssemblyGroup> desired)
    {
        for (int index = current.Count - 1; index >= 0; index--)
            if (!desired.Contains(current[index])) current.RemoveAt(index);
        for (int index = 0; index < desired.Count; index++)
        {
            int oldIndex = current.IndexOf(desired[index]);
            if (oldIndex < 0) current.Insert(index, desired[index]);
            else if (oldIndex != index) current.Move(oldIndex, index);
        }
    }

    private void NotifyPreferenceAvailability()
    {
        this.RaisePropertyChanged(nameof(CanEditAssemblyVisibility));
        this.RaisePropertyChanged(nameof(CanEditCollapsedAssemblies));
        this.RaisePropertyChanged(nameof(CanEditFavoriteAssemblies));
        foreach (ToolboxAssemblyGroup group in _assemblyGroups.Values) group.NotifyPreferencesChanged();
    }

    private void DisposeAssemblyPreferences()
    {
        Interlocked.Increment(ref _preferenceLoadVersion);
        if (_workspaceInfo is not null) _workspaceInfo.WorkspaceChanged -= OnPreferencesWorkspaceChanged;
    }
}
