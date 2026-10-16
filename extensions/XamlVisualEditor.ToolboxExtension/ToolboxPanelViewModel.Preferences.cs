using RxVoid = ReactiveUI.Primitives.RxVoid;
using System.Collections.ObjectModel;
using System.Reactive;
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
    // A stored list that cannot be read is not overwritten, so a later edit cannot replace what is still on disk.
    private readonly HashSet<string> _unreadablePreferenceKeys = new(StringComparer.Ordinal);
    private string? _preferenceError;
    private bool _preferencesReady = true;
    private int _preferenceLoadVersion;
    private int _preferenceSaveVersion;
    private Task _preferenceWrites = Task.CompletedTask;

    /// <summary>Gets the preference writes that have not finished yet.</summary>
    internal Task PendingPreferenceWrites => _preferenceWrites;

    public ObservableCollection<ToolboxAssemblyGroup> AssemblyChoices { get; } = new();
    public bool CanEditAssemblyPreferences => _preferencesReady && !IsLoading && !_disposed;
    public string AssemblyFilterSummary => string.Format(UiStrings.ToolboxAssembliesEnabled,
        AssemblyChoices.Count(group => group.IsIncluded), AssemblyChoices.Count);
    public string? PreferenceError
    {
        get => _preferenceError;
        private set => this.RaiseAndSetIfChanged(ref _preferenceError, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> ShowAllAssembliesCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> HideAllAssembliesCommand { get; }

    public Task InitializePreferencesAsync(CancellationToken cancellationToken = default) =>
        LoadAssemblyPreferencesAsync(_workspaceInfo?.WorkspacePath, cancellationToken);

    private void OnPreferencesWorkspaceChanged(object? sender, WorkspaceChangedEventArgs args) =>
        _ = LoadAssemblyPreferencesAsync(args.WorkspacePath, CancellationToken.None);

    private async Task LoadAssemblyPreferencesAsync(string? workspacePath, CancellationToken cancellationToken)
    {
        if (_disposed) return;
        int version = Interlocked.Increment(ref _preferenceLoadVersion);
        _preferencesReady = false;
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
                PreferenceError = null;
                NotifyPreferenceAvailability();
            }).ConfigureAwait(false);
            if (!IsCurrentPreferenceLoad(version)) return;

            List<string> errors = new();
            string[]? hidden = await TryReadPreferenceAsync(visibilityKey, errors, cancellationToken).ConfigureAwait(false);
            string[]? collapsed = await TryReadPreferenceAsync(collapseKey, errors, cancellationToken).ConfigureAwait(false);
            string[]? favorites = await TryReadPreferenceAsync(FavoriteAssembliesKey, errors, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await UpdateUiAsync(() =>
            {
                if (!IsCurrentPreferenceLoad(version)) return;
                _unreadablePreferenceKeys.Clear();
                ApplyStoredPreference(_hiddenAssemblies, hidden, visibilityKey);
                ApplyStoredPreference(_collapsedAssemblies, collapsed, collapseKey);
                ApplyStoredPreference(_favoriteAssemblies, favorites, FavoriteAssembliesKey);
                PreferenceError = errors.Count == 0 ? null
                    : string.Format(UiStrings.ToolboxPreferencesLoadFailed, string.Join(" ", errors));
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
                PreferenceError = string.Format(UiStrings.ToolboxPreferencesLoadFailed, error.Message);
                NotifyPreferenceAvailability();
            }).ConfigureAwait(false);
        }
    }

    private async Task<string[]?> TryReadPreferenceAsync(string key, List<string> errors, CancellationToken cancellationToken)
    {
        if (_preferenceStorage is null) return Array.Empty<string>();
        try
        {
            return await _preferenceStorage.GetAsync<string[]>(key, cancellationToken).ConfigureAwait(false) ?? Array.Empty<string>();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            errors.Add(error.Message);
            return null;
        }
    }

    private void ApplyStoredPreference(HashSet<string> current, string[]? stored, string key)
    {
        if (stored is null)
        {
            _unreadablePreferenceKeys.Add(key);
            return;
        }

        current.Clear();
        current.UnionWith(stored.Where(name => name is not null));
    }

    private bool IsCurrentPreferenceLoad(int version) => !_disposed && version == Volatile.Read(ref _preferenceLoadVersion);

    internal bool IsAssemblyIncluded(string name) => !_hiddenAssemblies.Contains(name);
    internal bool IsAssemblyFavorite(string name) => _favoriteAssemblies.Contains(name);

    internal void SetAssemblyExpanded(ToolboxAssemblyGroup group, bool expanded)
    {
        if (!CanEditAssemblyPreferences) return;
        group.ApplyExpandedState(expanded);
        bool changed = expanded ? _collapsedAssemblies.Remove(group.AssemblyName) : _collapsedAssemblies.Add(group.AssemblyName);
        if (changed) SaveCollapsedAssemblies();
    }

    private void SaveCollapsedAssemblies() =>
        _ = SaveAssemblyPreferencesAsync(_collapseKey, _collapsedAssemblies.OrderBy(name => name, StringComparer.Ordinal).ToArray());

    internal void SetAssemblyIncluded(ToolboxAssemblyGroup group, bool included)
    {
        if (!CanEditAssemblyPreferences) return;
        bool changed = included ? _hiddenAssemblies.Remove(group.AssemblyName) : _hiddenAssemblies.Add(group.AssemblyName);
        if (!changed) return;
        ApplyFilter();
        _ = SaveAssemblyPreferencesAsync(_visibilityKey, _hiddenAssemblies.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    internal void SetAssemblyFavorite(ToolboxAssemblyGroup group, bool favorite)
    {
        if (!CanEditAssemblyPreferences || !group.CanFavorite) return;
        bool changed = favorite ? _favoriteAssemblies.Add(group.AssemblyName) : _favoriteAssemblies.Remove(group.AssemblyName);
        if (!changed) return;
        ApplyFilter();
        _ = SaveAssemblyPreferencesAsync(FavoriteAssembliesKey, _favoriteAssemblies.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    private void SetAllAssembliesIncluded(bool included)
    {
        if (!CanEditAssemblyPreferences) return;
        bool changed = false;
        foreach (ToolboxAssemblyGroup group in AssemblyChoices)
            changed |= included ? _hiddenAssemblies.Remove(group.AssemblyName) : _hiddenAssemblies.Add(group.AssemblyName);
        if (!changed) return;
        ApplyFilter();
        _ = SaveAssemblyPreferencesAsync(_visibilityKey, _hiddenAssemblies.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    private async Task SaveAssemblyPreferencesAsync(string key, string[] values)
    {
        int version = Interlocked.Increment(ref _preferenceSaveVersion);
        if (_unreadablePreferenceKeys.Count == 0) PreferenceError = null;
        if (_preferenceStorage is not { } storage || _unreadablePreferenceKeys.Contains(key)) return;
        // The storage writes and flushes synchronously; the writes run in order away from the UI thread.
        Task write = _preferenceWrites.ContinueWith(_ => storage.SetAsync(key, values, CancellationToken.None),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _preferenceWrites = write;
        try { await write.ConfigureAwait(false); }
        catch (Exception error)
        {
            await UpdateUiAsync(() =>
            {
                if (!_disposed && version == Volatile.Read(ref _preferenceSaveVersion))
                    PreferenceError = string.Format(UiStrings.ToolboxPreferencesSaveFailed, error.Message);
            }).ConfigureAwait(false);
        }
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
        this.RaisePropertyChanged(nameof(CanEditAssemblyPreferences));
        foreach (ToolboxAssemblyGroup group in _assemblyGroups.Values) group.NotifyPreferencesChanged();
    }

    private void DisposeAssemblyPreferences()
    {
        Interlocked.Increment(ref _preferenceLoadVersion);
        if (_workspaceInfo is not null) _workspaceInfo.WorkspaceChanged -= OnPreferencesWorkspaceChanged;
    }
}
