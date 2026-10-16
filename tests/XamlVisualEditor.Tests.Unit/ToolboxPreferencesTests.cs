using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.App.Services;
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
            await WaitUntilAsync(() => model.CanEditAssemblyPreferences && LibraryGroup(model).IsIncluded);
            Assert.True(LibraryGroup(model).IsFavorite);
            Assert.True(LibraryGroup(model).IsExpanded);

            workspace.UpdateWorkspacePath(first);
            await WaitUntilAsync(() => model.CanEditAssemblyPreferences && !LibraryGroup(model).IsIncluded);
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
        Assert.NotNull(reopened.PreferenceError);

        library.IsFavorite = true;
        library.IsIncluded = true;
        await reopened.PendingPreferenceWrites;
        Assert.Empty(Assert.IsType<string[]>(await storage.GetAsync<string[]>(
            "toolbox.assemblyVisibility:" + VisibilityIdentity(workspace.WorkspacePath!), CancellationToken.None)));
        Assert.Equal("not a list", await storage.GetAsync<string>(FavoritesKey, CancellationToken.None));
        Assert.NotNull(reopened.PreferenceError);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static async Task<ToolboxPanelViewModel> CreateLoadedModelAsync(WorkspaceInfoService workspace, FileExtensionStorage storage)
    {
        MetadataControlCatalog catalog = new(TypeMetadataService.ReadControlTypes);
        catalog.SetWorkspaceAssemblies(new[] { typeof(CaptionControl).Assembly.Location });
        ToolboxPanelViewModel model = new(new CommandRegistry(), new InMemorySettingsStore(), catalog,
            new ReactiveUI.Primitives.Concurrency.SynchronizationContextSequencer(SynchronizationContext.Current!),
            workspaceInfo: workspace, storage: storage);
        await model.InitializePreferencesAsync();
        await model.ReloadAsync();
        await WaitUntilAsync(() => model.CanEditAssemblyPreferences);
        return model;
    }

    private static ToolboxAssemblyGroup LibraryGroup(ToolboxPanelViewModel model) =>
        Assert.Single(model.AssemblyChoices, group => group.AssemblyName == typeof(CaptionControl).Assembly.GetName().Name);

    private static string VisibilityIdentity(string workspacePath)
    {
        string identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        return OperatingSystem.IsWindows() ? identity.ToUpperInvariant() : identity;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The toolbox preferences did not reach the expected state.");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
