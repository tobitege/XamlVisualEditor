using Avalonia.Headless.XUnit;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.TestControls;
using XamlVisualEditor.ToolboxExtension;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ToolboxPreferencesTests : IDisposable
{
    private const string FavoritesKey = "toolbox.assemblyFavorites";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xve-toolbox-preferences-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task HiddenAndCollapsedAssembliesFollowTheWorkspaceWhileFavoritesApplyToEveryWorkspace()
    {
        string first = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(_root, "second")).FullName;
        WorkspaceInfoService workspace = new();
        workspace.UpdateWorkspacePath(first);
        FileExtensionStorage storage = new("toolbox", Path.Combine(_root, "storage"));

        using (ToolboxPanelViewModel model = await CreateLoadedModelAsync(workspace, storage))
        {
            ToolboxAssemblyGroup library = LibraryGroup(model);
            library.IsFavorite = true;
            library.IsExpanded = false;
            library.IsIncluded = false;
            Assert.False(library.IsIncluded);

            workspace.UpdateWorkspacePath(second);
            await model.PendingPreferenceLoad;
            Assert.True(model.CanEditAssemblyVisibility);
            Assert.True(LibraryGroup(model).IsIncluded);
            Assert.True(LibraryGroup(model).IsFavorite);
            Assert.True(LibraryGroup(model).IsExpanded);

            workspace.UpdateWorkspacePath(first);
            await model.PendingPreferenceLoad;
            Assert.True(model.CanEditAssemblyVisibility);
            Assert.False(LibraryGroup(model).IsIncluded);
            Assert.False(LibraryGroup(model).IsExpanded);
            await model.PendingPreferenceWrites;
        }

        // A new view model reads the lists back from the storage.
        using ToolboxPanelViewModel reopened = await CreateLoadedModelAsync(workspace, storage);
        ToolboxAssemblyGroup reloaded = LibraryGroup(reopened);
        Assert.False(reloaded.IsIncluded);
        Assert.False(reloaded.IsExpanded);
        Assert.True(reloaded.IsFavorite);
        Assert.Null(reopened.PreferenceError);
    }

    [AvaloniaFact]
    public async Task AnUnreadableListKeepsTheOthersAndIsNotOverwritten()
    {
        WorkspaceInfoService workspace = new();
        workspace.UpdateWorkspacePath(Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName);
        FileExtensionStorage storage = new("toolbox", Path.Combine(_root, "storage"));
        using (ToolboxPanelViewModel model = await CreateLoadedModelAsync(workspace, storage))
        {
            LibraryGroup(model).IsIncluded = false;
            await model.PendingPreferenceWrites;
        }

        await storage.SetAsync(FavoritesKey, "not a list", CancellationToken.None);

        using ToolboxPanelViewModel reopened = await CreateLoadedModelAsync(workspace, storage);
        ToolboxAssemblyGroup library = LibraryGroup(reopened);
        Assert.False(library.IsIncluded);
        Assert.False(library.IsFavorite);
        string loadError = Assert.IsType<string>(reopened.PreferenceError);

        // Only the unreadable list is locked; an edit of it would be lost instead of saved.
        Assert.False(reopened.CanEditFavoriteAssemblies);
        Assert.False(library.CanEditFavorite);
        Assert.True(library.CanEditIncluded);
        Assert.True(library.CanEditExpanded);
        library.IsFavorite = true;
        Assert.False(library.IsFavorite);
        library.IsIncluded = true;
        await reopened.PendingPreferenceSaves;
        Assert.Empty(Assert.IsType<string[]>(await storage.GetAsync<string[]>(
            "toolbox.assemblyVisibility:" + VisibilityIdentity(workspace.WorkspacePath!), CancellationToken.None)));
        Assert.Equal("not a list", await storage.GetAsync<string>(FavoritesKey, CancellationToken.None));
        Assert.Equal(loadError, reopened.PreferenceError);
    }

    [AvaloniaFact]
    public async Task AFailedWriteKeepsItsErrorUntilTheSameListIsSaved()
    {
        WorkspaceInfoService workspace = new();
        workspace.UpdateWorkspacePath(Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName);
        FailingStorage storage = new() { FailingKey = FavoritesKey };
        using ToolboxPanelViewModel model = await CreateLoadedModelAsync(workspace, storage);
        ToolboxAssemblyGroup library = LibraryGroup(model);

        library.IsFavorite = true;
        await model.PendingPreferenceSaves;
        string saveError = string.Format(UiStrings.ToolboxPreferencesSaveFailed, FailingStorage.ERROR_MESSAGE);
        Assert.Equal(saveError, model.PreferenceError);

        // A successful write of another list does not hide the failed one.
        library.IsExpanded = false;
        await model.PendingPreferenceSaves;
        Assert.Equal(saveError, model.PreferenceError);

        storage.FailingKey = null;
        library.IsFavorite = false;
        await model.PendingPreferenceSaves;
        Assert.Null(model.PreferenceError);
    }

    [AvaloniaFact]
    public async Task ExpandingAndFavoritesStayEditableWhileTheCatalogLoads()
    {
        ControllableControlCatalog catalog = new();
        InMemoryExtensionStorage storage = new();
        using ToolboxPanelViewModel model = new(new CommandRegistry(), new InMemorySettingsStore(), catalog,
            new ReactiveUI.Primitives.Concurrency.SynchronizationContextSequencer(SynchronizationContext.Current!),
            storage: storage);
        await model.InitializePreferencesAsync();
        Task loading = model.ReloadAsync();
        (await catalog.WaitForRequestAsync(0).WaitAsync(TimeSpan.FromSeconds(10)))
            .Complete(ControllableControlCatalog.ProjectControl("ProjectButton"));
        await loading;

        loading = model.ReloadAsync();
        ControllableControlCatalog.Request pending = await catalog.WaitForRequestAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(model.IsLoading);
        ToolboxAssemblyGroup library = Assert.Single(model.AssemblyChoices, group => group.AssemblyName == "Project.Controls");
        Assert.False(library.CanEditIncluded);
        Assert.True(library.CanEditExpanded);
        Assert.True(library.CanEditFavorite);
        library.IsExpanded = false;
        library.IsFavorite = true;
        Assert.False(library.IsExpanded);
        Assert.True(library.IsFavorite);
        await model.PendingPreferenceSaves;
        Assert.Equal(new[] { "Project.Controls" }, await storage.GetAsync<string[]>(FavoritesKey, CancellationToken.None));
        Assert.Null(model.PreferenceError);

        pending.Complete(ControllableControlCatalog.ProjectControl("ProjectButton"));
        await loading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(library.CanEditIncluded);
    }

    [AvaloniaFact]
    public async Task DisposeWaitsForThePendingWrite()
    {
        WorkspaceInfoService workspace = new();
        workspace.UpdateWorkspacePath(Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName);
        FailingStorage storage = new();
        ToolboxPanelViewModel model = await CreateLoadedModelAsync(workspace, storage);
        storage.HoldWrites();
        LibraryGroup(model).IsFavorite = true;
        await storage.WriteStarted.WaitAsync(TimeSpan.FromSeconds(10));
        _ = Task.Run(storage.ReleaseWrites);
        model.Dispose();
        Assert.True(model.PendingPreferenceWrites.IsCompletedSuccessfully);
        string libraryName = Assert.IsType<string>(typeof(CaptionControl).Assembly.GetName().Name);
        Assert.Equal(new[] { libraryName }, await storage.GetAsync<string[]>(FavoritesKey, CancellationToken.None));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static async Task<ToolboxPanelViewModel> CreateLoadedModelAsync(WorkspaceInfoService workspace, IExtensionStorage storage)
    {
        MetadataControlCatalog catalog = new(TypeMetadataService.ReadControlTypes);
        catalog.SetWorkspaceAssemblies(new[] { typeof(CaptionControl).Assembly.Location });
        ToolboxPanelViewModel model = new(new CommandRegistry(), new InMemorySettingsStore(), catalog,
            new ReactiveUI.Primitives.Concurrency.SynchronizationContextSequencer(SynchronizationContext.Current!),
            workspaceInfo: workspace, storage: storage);
        await model.InitializePreferencesAsync();
        await model.ReloadAsync();
        Assert.True(model.CanEditAssemblyVisibility);
        return model;
    }

    private static ToolboxAssemblyGroup LibraryGroup(ToolboxPanelViewModel model) =>
        Assert.Single(model.AssemblyChoices, group => group.AssemblyName == typeof(CaptionControl).Assembly.GetName().Name);

    private static string VisibilityIdentity(string workspacePath)
    {
        string identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        return OperatingSystem.IsWindows() ? identity.ToUpperInvariant() : identity;
    }

    /// <summary>In-memory storage that can fail the writes of one key and hold writes until a test releases them.</summary>
    private sealed class FailingStorage : IExtensionStorage
    {
        public const string ERROR_MESSAGE = "The storage is full.";
        private readonly InMemoryExtensionStorage _inner = new();
        private readonly ManualResetEventSlim _writesReleased = new(initialState: true);
        private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? FailingKey { get; set; }

        public Task WriteStarted => _writeStarted.Task;

        public void HoldWrites() => _writesReleased.Reset();

        public void ReleaseWrites() => _writesReleased.Set();

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) => _inner.GetAsync<T>(key, cancellationToken);

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
        {
            _writeStarted.TrySetResult();
            _writesReleased.Wait(cancellationToken);
            return key == FailingKey ? Task.FromException(new IOException(ERROR_MESSAGE)) : _inner.SetAsync(key, value, cancellationToken);
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken) => _inner.RemoveAsync(key, cancellationToken);
    }
}
