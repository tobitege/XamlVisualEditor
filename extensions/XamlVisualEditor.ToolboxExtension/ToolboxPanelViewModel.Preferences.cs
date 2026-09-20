using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
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
    private string? _preferenceError;
    private bool _preferencesReady = true;
    private int _preferenceLoadVersion;
    private int _preferenceSaveVersion;

    public ObservableCollection<ToolboxAssemblyGroup> AssemblyChoices { get; } = new();
    public bool CanEditAssemblyPreferences => _preferencesReady && !IsLoading && !_disposed;
    public string AssemblyFilterSummary => $"{AssemblyChoices.Count(group => group.IsIncluded)} of {AssemblyChoices.Count} assemblies enabled for this workspace";
    public string? PreferenceError
    {
        get => _preferenceError;
        private set => this.RaiseAndSetIfChanged(ref _preferenceError, value);
    }

    public ReactiveCommand<Unit, Unit> ShowAllAssembliesCommand { get; }
    public ReactiveCommand<Unit, Unit> HideAllAssembliesCommand { get; }

    public Task InitializePreferencesAsync(CancellationToken cancellationToken = default) =>
        LoadAssemblyPreferencesAsync(_workspaceInfo?.WorkspacePath, cancellationToken);

    private void OnPreferencesWorkspaceChanged(object? sender, WorkspaceChangedEventArgs args) =>
        _ = LoadAssemblyPreferencesAsync(args.WorkspacePath, CancellationToken.None);

    private async Task LoadAssemblyPreferencesAsync(string? workspacePath, CancellationToken cancellationToken)
    {
        if (_disposed) return;
        int version = Interlocked.Increment(ref _preferenceLoadVersion);
        _preferencesReady = false;
        bool scopeAssigned = false;
        try
        {
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
                scopeAssigned = true;
                PreferenceError = null;
                NotifyPreferenceAvailability();
            }).ConfigureAwait(false);
            if (!IsCurrentPreferenceLoad(version)) return;

            string[] hidden = _preferenceStorage is null ? Array.Empty<string>() :
                await _preferenceStorage.GetAsync<string[]>(visibilityKey, cancellationToken).ConfigureAwait(false) ?? Array.Empty<string>();
            string[] collapsed = _preferenceStorage is null ? Array.Empty<string>() :
                await _preferenceStorage.GetAsync<string[]>(collapseKey, cancellationToken).ConfigureAwait(false) ?? Array.Empty<string>();
            string[] favorites = _preferenceStorage is null ? Array.Empty<string>() :
                await _preferenceStorage.GetAsync<string[]>(FavoriteAssembliesKey, cancellationToken).ConfigureAwait(false) ?? Array.Empty<string>();
            cancellationToken.ThrowIfCancellationRequested();
            await UpdateUiAsync(() =>
            {
                if (!IsCurrentPreferenceLoad(version)) return;
                _hiddenAssemblies.Clear();
                _hiddenAssemblies.UnionWith(hidden.Where(name => name is not null));
                _collapsedAssemblies.Clear();
                _collapsedAssemblies.UnionWith(collapsed.Where(name => name is not null));
                _favoriteAssemblies.Clear();
                _favoriteAssemblies.UnionWith(favorites.Where(name => name is not null));
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
                _preferencesReady = scopeAssigned;
                PreferenceError = "Cannot load toolbox preferences: " + error.Message;
                NotifyPreferenceAvailability();
            }).ConfigureAwait(false);
        }
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
        PreferenceError = null;
        if (_preferenceStorage is null) return;
        try { await _preferenceStorage.SetAsync(key, values, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error)
        {
            await UpdateUiAsync(() =>
            {
                if (!_disposed && version == Volatile.Read(ref _preferenceSaveVersion))
                    PreferenceError = "Cannot save toolbox preferences: " + error.Message;
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
            group.UpdateCatalogInfo(assembly.Count(), !string.IsNullOrEmpty(assembly.Key) && assembly.Any(item => !item.IsStandardControl));
            group.ApplyExpandedState(!_collapsedAssemblies.Contains(group.AssemblyName));
        }
        SynchronizeGroups(AssemblyChoices, _assemblyGroups.Values
            .OrderBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
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
