using Avalonia.Headless.XUnit;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;
using SampleThemeProvider = XamlVisualEditor.TestControls.SampleThemeProvider;

namespace XamlVisualEditor.Tests.Unit;

[Collection(nameof(PreviewerHostEnvironmentCollection))]
public sealed class DesignerThemePreferencesTests
{
    private const string Source = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button /></Canvas>";
    private static readonly DesignerThemeSelection Harbor = new("sample.palette", "harbor");
    private static readonly DesignerThemeSelection Clay = new("sample.palette", "clay");

    private static ExtensionContributionRegistry Providers()
    {
        ExtensionContributionRegistry registry = new();
        registry.RegisterDesignerThemeProviders("sample.extension", new[] { new DesignerThemeProviderRegistration(
            "sample.palette", "Sample", typeof(SampleThemeProvider).Assembly.Location, typeof(SampleThemeProvider).FullName!) });
        return registry;
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task ThemeSelectionIsSharedByDocumentsAndRestoredFromWorkspaceScopedFiles()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string storageRoot = Path.Combine(Path.GetTempPath(), "XamlVisualEditor-theme-preference-tests");
        string workspaceA = Path.Combine(storageRoot, "WorkspaceA.slnx");
        string workspaceB = Path.Combine(storageRoot, "WorkspaceB.slnx");
        FileExtensionStorage storage = new("xve.designerThemes", storageRoot);
        await storage.SetAsync(MainWindowViewModel.GetPreviewThemePreferenceKey(workspaceA), Harbor, cancellationToken);
        await storage.SetAsync<DesignerThemeSelection?>(MainWindowViewModel.GetPreviewThemePreferenceKey(workspaceB), null, cancellationToken);
        WorkspaceInfoService workspace = new();
        workspace.UpdateWorkspacePath(workspaceA);
        using MainWindowViewModel main = new(extensionContributionRegistry: Providers(), themeWorkspaceInfo: workspace,
            extensionStorageFactory: _ => storage);
        await main.ThemePreferencesLoaded;
        var updates = System.Threading.Channels.Channel.CreateUnbounded<(string Path, DesignerThemeSelection? Selection)>();
        Task UpdatePreviewer(DesignerDocumentViewModel document)
        {
            updates.Writer.TryWrite((document.FilePath, document.PreviewThemeRequest?.Selection));
            return Task.CompletedTask;
        }
        using DesignerDocumentViewModel first = new("First.axaml", updatePreviewerAsync: UpdatePreviewer);
        using DesignerDocumentViewModel second = new("Second.axaml", updatePreviewerAsync: UpdatePreviewer);
        await first.SyncEngine.LoadAsync(Source).WaitAsync(cancellationToken);
        await second.SyncEngine.LoadAsync(Source).WaitAsync(cancellationToken);
        main.Documents.Add(first);
        main.Documents.Add(second);
        main.ActiveDocument = second;
        Assert.Equal(Harbor, first.PreviewThemeRequest?.Selection);
        Assert.Equal(Harbor, second.PreviewThemeRequest?.Selection);
        second.DesignSurface.SelectedPreviewTheme = new DesignerThemeChoice(Clay, "Clay");
        await main.ThemePreferencesSaved;
        HashSet<string> updatedPaths = new(StringComparer.Ordinal);
        while (updatedPaths.Count < 2)
        {
            var update = await updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (update.Selection == Clay) updatedPaths.Add(update.Path);
        }
        Assert.Contains(first.FilePath, updatedPaths);
        Assert.Contains(second.FilePath, updatedPaths);
        Assert.Same(second, main.ActiveDocument);
        Assert.Equal(Clay, second.PreviewThemeRequest?.Selection);
        Assert.Equal(Source, first.SyncEngine.GetCurrentText());
        Assert.Equal(Source, second.SyncEngine.GetCurrentText());
        Assert.Equal(0, first.SyncEngine.UndoRedo.UndoCount);
        Assert.Equal(0, second.SyncEngine.UndoRedo.UndoCount);

        workspace.UpdateWorkspacePath(workspaceB);
        await main.ThemePreferencesLoaded;
        Assert.Null(first.PreviewThemeRequest?.Selection);
        Assert.Null(second.PreviewThemeRequest?.Selection);
        second.DesignSurface.SelectedPreviewTheme = new DesignerThemeChoice(Harbor, "Harbor");
        await main.ThemePreferencesSaved;

        FileExtensionStorage reopenedStorage = new("xve.designerThemes", storageRoot);
        WorkspaceInfoService reopenedWorkspace = new();
        reopenedWorkspace.UpdateWorkspacePath(workspaceA);
        using MainWindowViewModel reopened = new(extensionContributionRegistry: Providers(), themeWorkspaceInfo: reopenedWorkspace,
            extensionStorageFactory: _ => reopenedStorage);
        await reopened.ThemePreferencesLoaded;
        using DesignerDocumentViewModel third = new("Reopened.axaml");
        reopened.Documents.Add(third);
        Assert.Equal(Clay, third.PreviewThemeRequest?.Selection);
        reopenedWorkspace.UpdateWorkspacePath(workspaceB);
        await reopened.ThemePreferencesLoaded;
        Assert.Equal(Harbor, third.PreviewThemeRequest?.Selection);
        third.DesignSurface.SelectedPreviewTheme = DesignerThemeChoice.ProjectDefault;
        await reopened.ThemePreferencesSaved;
        Assert.Null(await reopenedStorage.GetAsync<DesignerThemeSelection>(MainWindowViewModel.GetPreviewThemePreferenceKey(workspaceB), cancellationToken));
        Assert.Equal(Clay, await reopenedStorage.GetAsync<DesignerThemeSelection>(MainWindowViewModel.GetPreviewThemePreferenceKey(workspaceA), cancellationToken));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task LateWorkspacePreferenceReadCannotReplaceTheCurrentWorkspaceSelection()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string workspaceA = Path.Combine(Path.GetTempPath(), "ThemeWorkspaceA.slnx");
        string workspaceB = Path.Combine(Path.GetTempPath(), "ThemeWorkspaceB.slnx");
        DelayedStorage storage = new(MainWindowViewModel.GetPreviewThemePreferenceKey(workspaceA));
        await storage.SetAsync(MainWindowViewModel.GetPreviewThemePreferenceKey(workspaceA), Clay, cancellationToken);
        await storage.SetAsync(MainWindowViewModel.GetPreviewThemePreferenceKey(workspaceB), Harbor, cancellationToken);
        WorkspaceInfoService workspace = new();
        workspace.UpdateWorkspacePath(workspaceA);
        using MainWindowViewModel main = new(extensionContributionRegistry: Providers(), themeWorkspaceInfo: workspace,
            extensionStorageFactory: _ => storage);
        Task firstLoad = main.ThemePreferencesLoaded;
        using DesignerDocumentViewModel document = new("LateTheme.axaml");
        main.Documents.Add(document);
        await storage.Started.Task;
        workspace.UpdateWorkspacePath(workspaceB);
        await main.ThemePreferencesLoaded;
        Assert.Equal(Harbor, document.PreviewThemeRequest?.Selection);
        storage.Release.TrySetResult();
        await firstLoad;
        Assert.Equal(Harbor, document.PreviewThemeRequest?.Selection);
    }

    private sealed class DelayedStorage(string delayedKey) : IExtensionStorage
    {
        private readonly InMemoryExtensionStorage _storage = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
        {
            if (key == delayedKey)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await _storage.GetAsync<T>(key, cancellationToken);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken) => _storage.SetAsync(key, value, cancellationToken);
        public Task RemoveAsync(string key, CancellationToken cancellationToken) => _storage.RemoveAsync(key, cancellationToken);
    }
}
