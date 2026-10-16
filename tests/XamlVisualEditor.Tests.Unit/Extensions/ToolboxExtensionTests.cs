using ReactiveUI.Primitives.Concurrency;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Debugging;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.ToolboxExtension;
using System.Reactive.Threading.Tasks;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using ToolboxExtensionEntry = XamlVisualEditor.ToolboxExtension.ToolboxExtension;
using Xunit;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Tests.Unit.Extensions;

#pragma warning disable CS0067

public sealed class ToolboxExtensionTests
{
    [Fact]
    public async Task ToolboxPanel_UsesSettingsCatalogAndCommandArgs()
    {
        RecordingCommands commands = new();
        InMemoryWindow window = new();
        InMemorySettingsStore settings = new();
        string assemblyName = Assert.IsType<string>(typeof(Avalonia.Controls.Border).Assembly.GetName().Name);

        await settings.UpdateAsync(
            "toolbox.catalog",
            new List<ToolboxCatalogEntry>
            {
                new()
                {
                    DisplayName = "Custom Border",
                    AssemblyName = assemblyName,
                    CommandId = "toolbox.insertSelected",
                    CommandArguments = new List<string>
                    {
                        "Border",
                        "https://github.com/avaloniaui",
                        "11111111-1111-1111-1111-111111111111"
                    }
                }
            },
            SettingsTarget.User,
            CancellationToken.None);

        using ToolboxPanelViewModel viewModel = new(commands, settings,
            workspaceInfo: new StubWorkspaceInfo { WorkspacePath = "test.csproj" }, designer: new StubDesignerHost());
        Assert.Single(viewModel.Items);
        Assert.Equal("Custom Border", viewModel.Items[0].DisplayName);

        ToolboxAssemblyGroup group = Assert.Single(viewModel.AssemblyGroups);
        Assert.Equal(assemblyName, group.AssemblyName);
        group.SelectedItem = Assert.Single(group.Items);
        await viewModel.InsertSelectedCommand.Execute().ToTask();

        Assert.Equal("toolbox.insertSelected", commands.LastCommandId);
        Assert.Equal(3, commands.LastArguments?.Count ?? 0);
        Assert.Equal("Border", commands.LastArguments?[0] as string);
        Assert.Equal("https://github.com/avaloniaui", commands.LastArguments?[1] as string);
        Assert.Equal("11111111-1111-1111-1111-111111111111", commands.LastArguments?[2] as string);
        viewModel.SearchText = "no matching custom entry";
        Assert.Empty(viewModel.AssemblyGroups);
        Assert.Null(viewModel.SelectedItem);
        await viewModel.ClearFiltersCommand.Execute().ToTask();
        Assert.Same(group, Assert.Single(viewModel.AssemblyGroups));
        // The filter removed the selection; clearing the filter does not select a control on its own.
        Assert.Null(viewModel.SelectedItem);
        Assert.Null(group.SelectedItem);
        Assert.False(await viewModel.InsertSelectedCommand.CanExecute.FirstAsync());
    }

    [Fact]
    public async Task EveryToolboxCommandAppearsInTheCommandPalette()
    {
        ExtensionContext context = CreateContext(new CommandRegistry(), out _, out _);

        await new ToolboxExtensionEntry().ActivateAsync(context, CancellationToken.None);

        ExtensionContributionRegistry contributions = Assert.IsType<ExtensionContributionRegistry>(context.Contributions);
        foreach (string commandId in new[]
                 {
                     "toolbox.insertButton", "toolbox.insertSelected", "toolbox.showLoadTask", "toolbox.toggleView"
                 })
        {
            Assert.True(context.CommandMetadata.TryGet(commandId, out CommandMetadata metadata), commandId);
            Assert.False(string.IsNullOrWhiteSpace(metadata.Title), commandId);
            Assert.Contains(contributions.CommandPaletteItems, item => item.CommandId == commandId);
        }
    }

    [Fact]
    public async Task InsertSelected_UsesCommandArguments()
    {
        CommandRegistry commands = new();
        ExtensionContext context = CreateContext(commands, out StubDesignerHost designerHost, out InMemoryWindow window);

        ToolboxExtensionEntry extension = new();
        await extension.ActivateAsync(context, CancellationToken.None);

        await commands.ExecuteAsync(
            "toolbox.insertSelected",
            new object?[] { "StackPanel", "https://github.com/avaloniaui" },
            CancellationToken.None);

        Assert.Equal("StackPanel", designerHost.LastTypeName);
        Assert.Equal("https://github.com/avaloniaui", designerHost.LastXmlNamespace);
        Assert.Null(designerHost.LastParentNodeId);
    }

    [Fact]
    public async Task InsertSelected_ShowsWarning_WhenArgumentsMissing()
    {
        CommandRegistry commands = new();
        ExtensionContext context = CreateContext(commands, out StubDesignerHost _, out InMemoryWindow window);

        ToolboxExtensionEntry extension = new();
        await extension.ActivateAsync(context, CancellationToken.None);

        await commands.ExecuteAsync(
            "toolbox.insertSelected",
            new object?[] { "Button" },
            CancellationToken.None);

        Assert.Contains(UiStrings.ToolboxArgumentsRequired, window.Messages);
    }

    [Fact]
    public async Task InsertAvailabilityTracksTheDesignerDocumentWithOrWithoutAWorkspace()
    {
        RecordingCommands commands = new();
        InMemorySettingsStore settings = new();
        await settings.UpdateAsync("toolbox.catalog", new List<ToolboxCatalogEntry>
        {
            new() { DisplayName = "Button", CommandId = "toolbox.insertSelected",
                CommandArguments = new List<string> { "Button", "https://github.com/avaloniaui" } }
        }, SettingsTarget.User, CancellationToken.None);
        StubWorkspaceInfo workspace = new();
        StubDesignerHost designer = new();
        designer.SetActiveDocument(null);
        using ToolboxPanelViewModel model = new(commands, settings, uiScheduler: ImmediateSequencer.Instance,
            workspaceInfo: workspace, designer: designer);
        model.SelectedItem = Assert.Single(model.Items);
        Assert.False(await model.InsertSelectedCommand.CanExecute.FirstAsync());
        // A loose XAML file opened without a project can receive controls as well.
        designer.SetActiveDocument("test.axaml");
        Assert.True(model.HasInsertTarget);
        Assert.True(await model.InsertSelectedCommand.CanExecute.FirstAsync());
        await model.InsertSelectedCommand.Execute().ToTask();
        Assert.Equal("toolbox.insertSelected", commands.LastCommandId);
        workspace.SetWorkspace("test.csproj");
        Assert.True(await model.InsertSelectedCommand.CanExecute.FirstAsync());
        designer.SetActiveDocument(null);
        Assert.False(model.HasInsertTarget);
        Assert.False(await model.InsertSelectedCommand.CanExecute.FirstAsync());
    }

    [Fact]
    public async Task MissingInsertTargetShowsAWarningWhenTheCommandIsInvokedDirectly()
    {
        CommandRegistry commands = new();
        ExtensionContext context = CreateContext(commands, out StubDesignerHost designer, out InMemoryWindow window);
        await new ToolboxExtensionEntry().ActivateAsync(context, CancellationToken.None);
        designer.SetActiveDocument(null);
        await commands.ExecuteAsync("toolbox.insertSelected", new object?[] { "Button", "https://github.com/avaloniaui" },
            CancellationToken.None);
        await commands.ExecuteAsync("toolbox.insertButton", null, CancellationToken.None);
        Assert.Equal(new[] { UiStrings.DesignerDocumentRequired, UiStrings.DesignerDocumentRequired }, window.Messages);
        Assert.Null(designer.LastTypeName);
        // Without a workspace the active designer document still receives the control.
        designer.SetActiveDocument("test.axaml");
        Assert.IsType<StubWorkspaceInfo>(context.WorkspaceInfo).SetWorkspace(null);
        await commands.ExecuteAsync("toolbox.insertButton", null, CancellationToken.None);
        Assert.Equal(2, window.Messages.Count);
        Assert.Equal("Button", designer.LastTypeName);
    }

    [Fact]
    public async Task ADesignerDocumentBehindTheActiveTextEditorIsNoInsertTarget()
    {
        RecordingCommands recording = new();
        InMemorySettingsStore settings = await CreateButtonCatalogSettingsAsync();
        StubDesignerHost designer = new();
        StubEditorServices editor = new();
        editor.SetActiveDocument("notes.txt");
        using ToolboxPanelViewModel model = new(recording, settings, uiScheduler: ImmediateSequencer.Instance,
            designer: designer, editor: editor);
        model.SelectedItem = Assert.Single(model.Items);
        Assert.Equal("test.axaml", designer.ActiveDocumentPath);
        Assert.False(model.HasInsertTarget);
        Assert.False(await model.InsertSelectedCommand.CanExecute.FirstAsync());
        editor.SetActiveDocument("test.axaml");
        Assert.True(model.HasInsertTarget);
        Assert.True(await model.InsertSelectedCommand.CanExecute.FirstAsync());

        // The command follows the same rule as the panel when it is invoked directly.
        CommandRegistry commands = new();
        ExtensionContext context = CreateContext(commands, out StubDesignerHost contextDesigner, out InMemoryWindow window);
        await new ToolboxExtensionEntry().ActivateAsync(context, CancellationToken.None);
        Assert.IsType<StubEditorServices>(context.Editor).SetActiveDocument("notes.txt");
        await commands.ExecuteAsync("toolbox.insertSelected", new object?[] { "Button", "https://github.com/avaloniaui" },
            CancellationToken.None);
        Assert.Null(contextDesigner.LastTypeName);
        Assert.Equal(UiStrings.DesignerDocumentRequired, Assert.Single(window.Messages));
    }

    [Fact]
    public async Task InsertWorksWhileTheCatalogLoadsAndAfterAFailedPreferenceLoad()
    {
        RecordingCommands commands = new();
        InMemorySettingsStore settings = await CreateButtonCatalogSettingsAsync();
        ControllableControlCatalog catalog = new();
        using ToolboxPanelViewModel model = new(commands, settings, catalog, ImmediateSequencer.Instance,
            storage: new InMemoryExtensionStorage(), designer: new StubDesignerHost());
        // The immediate sequencer would wait out the status dismissal delay inside the reload.
        model.StatusDismissalScheduler = new VirtualClock();
        // A cancelled load leaves the stored lists unread, which locks them but not the insertion.
        await model.InitializePreferencesAsync(new CancellationToken(canceled: true));
        Assert.NotNull(model.PreferenceError);
        Assert.False(model.CanEditAssemblyVisibility);
        Assert.False(model.CanEditCollapsedAssemblies);
        Assert.False(model.CanEditFavoriteAssemblies);
        model.SelectedItem = Assert.Single(model.Items);
        Assert.True(await model.InsertSelectedCommand.CanExecute.FirstAsync());

        Task loading = model.ReloadAsync();
        ControllableControlCatalog.Request request = await catalog.WaitForRequestAsync(0).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(model.IsLoading);
        Assert.True(await model.InsertSelectedCommand.CanExecute.FirstAsync());
        await model.InsertSelectedCommand.Execute().ToTask();
        Assert.Equal("toolbox.insertSelected", commands.LastCommandId);
        request.Complete();
        await loading.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ActivationShowsTheStandardControlsBeforeTheProjectScanFinishes()
    {
        ISequencer previousScheduler = ReactiveUI.RxSchedulers.MainThreadScheduler;
        ReactiveUI.RxSchedulers.MainThreadScheduler = AvaloniaScheduler.For(Avalonia.Threading.Dispatcher.UIThread);
        ControllableControlCatalog catalog = new() { HasProjectAssemblies = true };
        ExtensionContext context = CreateContext(new CommandRegistry(), out _, out _, catalog);
        try
        {
            await new ToolboxExtensionEntry().ActivateAsync(context, CancellationToken.None);
            Assert.True(Assert.IsType<ExtensionViewRegistry>(context.Views)
                .TryGetCustomViewProvider("toolbox.panel", out ICustomViewProvider provider));
            ToolboxPanelViewModel model = Assert.IsType<ToolboxPanelViewModel>(provider.CreateViewModel());

            ControllableControlCatalog.Request baseRequest = await catalog.WaitForRequestAsync(0).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(baseRequest.IncludeProjectControls);
            baseRequest.Complete(ControllableControlCatalog.StandardControl("Button"));
            ControllableControlCatalog.Request projectRequest = await catalog.WaitForRequestAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(projectRequest.IncludeProjectControls);
            // The standard controls show while the project scan still runs.
            Assert.Equal("Button", Assert.Single(model.Items).DisplayName);
            Assert.True(model.IsLoadingProjectControls);

            projectRequest.Complete(ControllableControlCatalog.StandardControl("Button"),
                ControllableControlCatalog.ProjectControl("ProjectButton"));
            await model.PendingCatalogLoad.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new[] { "Button", "ProjectButton" }, model.Items.Select(item => item.DisplayName).ToArray());
            Assert.False(model.IsLoading);
        }
        finally
        {
            foreach (IDisposable subscription in context.Subscriptions) subscription.Dispose();
            ReactiveUI.RxSchedulers.MainThreadScheduler = previousScheduler;
        }
    }

    private static async Task<InMemorySettingsStore> CreateButtonCatalogSettingsAsync()
    {
        InMemorySettingsStore settings = new();
        await settings.UpdateAsync("toolbox.catalog", new List<ToolboxCatalogEntry>
        {
            new() { DisplayName = "Button", CommandId = "toolbox.insertSelected",
                CommandArguments = new List<string> { "Button", "https://github.com/avaloniaui" } }
        }, SettingsTarget.User, CancellationToken.None);
        return settings;
    }

    private static ExtensionContext CreateContext(
        ICommands commands,
        out StubDesignerHost designerHost,
        out InMemoryWindow window,
        IControlCatalog? catalog = null)
    {
        window = new InMemoryWindow();
        designerHost = new StubDesignerHost();
        StubWorkspaceInfo workspaceInfo = new() { WorkspacePath = "test.csproj" };
        StubWorkspaceCommands workspaceCommands = new();
        StubEditorServices editorServices = new();
        StubNavigationService navigation = new();
        StubNavigationHistoryService navigationHistory = new();
        StubAnimationEditorHost animationHost = new();
        StubCollaborationHost collaborationHost = new();
        StubDebugSettingsHost debugSettingsHost = new();
        StubLspSettingsHost lspSettingsHost = new();
        StubExtensionViewHost viewHost = new();
        StubExtensionPermissions permissions = new();

        return new ExtensionContext(
            "test.toolbox",
            "/tmp",
            commands,
            new CommandMetadataRegistry(),
            new ExtensionContributionRegistry(),
            new DebuggerServiceRegistry(),
            designerHost,
            new InMemoryWorkspace(),
            new WorkspaceModelAdapter(workspaceInfo, workspaceCommands, new StubWorkspaceService()),
            workspaceInfo,
            new StubSystemIconService(),
            window,
            new InMemoryDialogHost(),
            new InMemoryWorkspaceHost(),
            new ExtensionViewRegistry(),
            new ExtensionLanguageServiceRegistry(),
            navigation,
            navigationHistory,
            animationHost,
            collaborationHost,
            collaborationHost,
            debugSettingsHost,
            lspSettingsHost,
            editorServices,
            new StubDiagnostics(),
            new PropertyEditorRegistry(),
            new StubTerminalBridge(),
            permissions,
            viewHost,
            new InMemorySettingsStore(),
            new InMemoryExtensionStorage(),
            new StubExtensionLogger(),
            new List<IDisposable>())
        {
            ControlCatalog = catalog
        };
    }

    private sealed class StubSystemIconService : ISystemIconService
    {
        public object? GetIcon(string? path, bool isDirectory, object? fallbackIcon = null, int iconSize = 16)
            => fallbackIcon;

        public object? GetFileIcon(string? path, object? fallbackIcon = null, int iconSize = 16)
            => fallbackIcon;

        public object? GetFolderIcon(string? path, object? fallbackIcon = null, int iconSize = 16)
            => fallbackIcon;
    }

    private sealed class StubExtensionViewHost : IExtensionViewHost
    {
        public event EventHandler<ExtensionViewVisibilityChangedEventArgs>? VisibilityChanged;

        public event EventHandler<ExtensionViewFocusChangedEventArgs>? FocusChanged;

        public Task ShowAsync(string viewId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ToggleAsync(string viewId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> IsVisibleAsync(string viewId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task ActivateAsync(string viewId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubExtensionPermissions : IExtensionPermissions
    {
        public event EventHandler<ExtensionPermissionAuditEventArgs>? AccessAudited;
        public event EventHandler<ExtensionPermissionChangedEventArgs>? Changed;

        public void Declare(IReadOnlyList<ExtensionCapabilityDeclaration> capabilities)
        {
        }

        public Task<ExtensionPermissionDecision> RequestAsync(string capabilityId, CancellationToken cancellationToken)
            => Task.FromResult(new ExtensionPermissionDecision(
                capabilityId,
                IsAllowed: true,
                IsRemembered: false,
                ExtensionPermissionDecisionSource.Prompt,
                DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<ExtensionPermissionEntry>> GetRememberedAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ExtensionPermissionEntry>>(Array.Empty<ExtensionPermissionEntry>());

        public Task ClearRememberedAsync(string? capabilityId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubDesignerHost : IDesignerHost
    {
        public string? LastTypeName { get; private set; }
        public string? LastXmlNamespace { get; private set; }
        public string? LastParentNodeId { get; private set; }

        public string? ActiveDocumentPath { get; private set; } = "test.axaml";

        public void SetActiveDocument(string? path)
        {
            ActiveDocumentPath = path;
            ActiveDocumentChanged?.Invoke(this, new DesignerDocumentChangedEventArgs(path));
        }

        public event EventHandler<DesignerDocumentChangedEventArgs>? ActiveDocumentChanged;

        public event EventHandler<DesignerSelectionChangedEventArgs>? SelectionChanged;

        public Task<IReadOnlyList<DesignerNodeSummary>> GetSelectedNodesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DesignerNodeSummary>>(Array.Empty<DesignerNodeSummary>());

        public Task<IReadOnlyList<DesignerNodeSummary>> GetVisualTreeAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DesignerNodeSummary>>(Array.Empty<DesignerNodeSummary>());

        public Task<IReadOnlyList<DesignerNodeSummary>> GetLogicalTreeAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DesignerNodeSummary>>(Array.Empty<DesignerNodeSummary>());

        public Task<IReadOnlyList<DesignerPropertyInfo>> GetPropertiesAsync(string nodeId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DesignerPropertyInfo>>(Array.Empty<DesignerPropertyInfo>());

        public Task<IReadOnlyList<DesignerEventInfo>> GetEventsAsync(string nodeId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DesignerEventInfo>>(Array.Empty<DesignerEventInfo>());

        public Task<bool> SetPropertyAsync(string nodeId, string propertyName, string? value, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<string?> InsertElementAsync(string typeName, string xmlNamespace, string? parentNodeId, CancellationToken cancellationToken)
        {
            LastTypeName = typeName;
            LastXmlNamespace = xmlNamespace;
            LastParentNodeId = parentNodeId;
            return Task.FromResult<string?>("node-1");
        }

        public Task<bool> DeleteNodeAsync(string nodeId, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<string?> WrapNodeAsync(string nodeId, string wrapperTypeName, string wrapperXmlNamespace, CancellationToken cancellationToken)
            => Task.FromResult<string?>("wrap-1");

        public Task<bool> SelectNodeAsync(string nodeId, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<bool> RevealNodeAsync(string nodeId, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public IDesignerTransaction BeginTransaction(string name) => new StubTransaction();

        private sealed class StubTransaction : IDesignerTransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public void Dispose() { }
        }
    }

    private sealed class StubNavigationService : ILanguageNavigationService
    {
        public Task<IReadOnlyList<LanguageLocation>> FindDefinitionsAsync(
            LanguagePositionContext context,
            CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LanguageLocation>>(Array.Empty<LanguageLocation>());
        }

        public Task<IReadOnlyList<LanguageLocation>> FindReferencesAsync(
            LanguagePositionContext context,
            CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LanguageLocation>>(Array.Empty<LanguageLocation>());
        }

        public Task<IReadOnlyList<LanguageLocation>> FindImplementationsAsync(
            LanguagePositionContext context,
            CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LanguageLocation>>(Array.Empty<LanguageLocation>());
        }

        public Task<IReadOnlyList<LanguageSymbol>> GetWorkspaceSymbolsAsync(
            LanguageSymbolQuery query,
            CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LanguageSymbol>>(Array.Empty<LanguageSymbol>());
        }

        public Task<LanguageRenameInfo?> PrepareRenameAsync(
            LanguagePositionContext context,
            CancellationToken ct)
        {
            return Task.FromResult<LanguageRenameInfo?>(null);
        }

        public Task<LanguageWorkspaceEdit?> RenameAsync(
            LanguageRenameContext context,
            CancellationToken ct)
        {
            return Task.FromResult<LanguageWorkspaceEdit?>(null);
        }

        public Task<IReadOnlyList<LanguageCodeAction>> GetCodeActionsAsync(
            LanguageCodeActionContext context,
            CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LanguageCodeAction>>(Array.Empty<LanguageCodeAction>());
        }

        public Task<LanguageCodeAction?> ResolveCodeActionAsync(LanguageCodeAction action, CancellationToken ct)
        {
            return Task.FromResult<LanguageCodeAction?>(action);
        }

        public Task<bool> ApplyCodeActionAsync(LanguageCodeAction action, CancellationToken ct)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class StubNavigationHistoryService : INavigationHistoryService
    {
        public bool CanNavigateBack => false;
        public bool CanNavigateForward => false;
        public event EventHandler<NavigationHistoryChangedEventArgs>? HistoryChanged;

        public Task<bool> NavigateBackAsync(CancellationToken ct)
        {
            return Task.FromResult(false);
        }

        public Task<bool> NavigateForwardAsync(CancellationToken ct)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class StubAnimationEditorHost : IAnimationEditorHost
    {
        public IAnimationEditorPanelModel? PanelModel => null;

        public IDisposable BeginTransaction(string name)
        {
            return System.Reactive.Disposables.Disposable.Empty;
        }

        public Task RefreshPreviewAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StubCollaborationHost : ICollaborationHost, ICollaborationPanelHost
    {
        public bool IsSessionActive => false;

        public string? SessionId => null;

        public string StatusMessage => string.Empty;

        public ICollaborationPanelModel? PanelModel => null;

        public event EventHandler<CollaborationSessionChangedEventArgs>? SessionChanged;

        public event EventHandler<CollaborationParticipantsChangedEventArgs>? ParticipantsChanged;

        public IReadOnlyList<CollaborationParticipantInfo> GetParticipants()
        {
            return Array.Empty<CollaborationParticipantInfo>();
        }

        public Task StartSessionAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<bool> JoinSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task LeaveSessionAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string?> CreateShareLinkAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<bool> InviteAsync(string invitee, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class StubDebugSettingsHost : IDebugSettingsHost
    {
        public event EventHandler<DebugSettingsChangedEventArgs>? Changed;

        public DebugSettingsState GetState()
        {
            return new DebugSettingsState(string.Empty, false, false, string.Empty);
        }

        public Task SetAdapterPathAsync(string adapterPath, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task SetAutoDownloadToolsAsync(bool autoDownloadTools, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DownloadNetcoredbgAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StubLspSettingsHost : ILspSettingsHost
    {
        public string SettingsPath => string.Empty;

        public event EventHandler<LspSettingsChangedEventArgs>? Changed;

        public Task<IReadOnlyList<LspServerSettings>> LoadServersAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<LspServerSettings>>(Array.Empty<LspServerSettings>());
        }

        public Task SaveServersAsync(IReadOnlyList<LspServerSettings> servers, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StubWorkspaceInfo : IWorkspaceInfo
    {
        public string? WorkspacePath { get; set; }
        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged;

        public void SetWorkspace(string? path)
        {
            WorkspacePath = path;
            WorkspaceChanged?.Invoke(this, new WorkspaceChangedEventArgs(path));
        }
    }

    private sealed class StubWorkspaceCommands : IWorkspaceCommands
    {
        public bool HasWorkspace => true;
        public Task LoadWorkspaceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RestoreWorkspaceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task BuildWorkspaceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RebuildWorkspaceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CleanWorkspaceAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetStartupProjectAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UndoAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RedoAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CopyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PasteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SelectAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RenameSymbolAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FormatDocumentAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShowCodeActionsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShowDocumentSymbolsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShowWorkspaceSymbolsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ToggleBreakpointsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ToggleCallStackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ToggleLocalsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ToggleWatchesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartDebugAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopDebugAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ContinueDebugAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StepOverAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StepInAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StepOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PauseDebugAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ToggleBreakpointAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartRunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopRunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NewTerminalAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Editor whose active document matches the default document of <see cref="StubDesignerHost"/>.</summary>
    private sealed class StubEditorServices : IEditorServices
    {
        public IEditorDocument? ActiveDocument { get; private set; } = new StubEditorDocument("test.axaml");
        public IReadOnlyList<IEditorDocument> GetOpenDocuments() => Array.Empty<IEditorDocument>();
        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, CancellationToken ct) => Task.FromResult<IEditorDocument?>(null);
        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, EditorDocumentOpenBehavior behavior, CancellationToken ct) => Task.FromResult<IEditorDocument?>(null);
        public Task<bool> OpenLocationAsync(LanguageLocation location, CancellationToken ct) => Task.FromResult(false);
        public event EventHandler<EditorActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

        public void SetActiveDocument(string? path)
        {
            ActiveDocument = path is null ? null : new StubEditorDocument(path);
            ActiveDocumentChanged?.Invoke(this, new EditorActiveDocumentChangedEventArgs { Document = ActiveDocument });
        }
    }

    private sealed class StubEditorDocument(string filePath) : IEditorDocument
    {
        public string FilePath { get; } = filePath;
        public string? LanguageId => null;
        public int CaretOffset { get; set; }
        public int SelectionStart { get; set; }
        public int SelectionLength { get; set; }
        public Task<string> GetTextAsync(CancellationToken ct) => Task.FromResult(string.Empty);
        public Task ApplyEditsAsync(IReadOnlyList<TextEdit> edits, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<EditorDocumentChangedEventArgs>? Changed;
        public event EventHandler<EditorSelectionChangedEventArgs>? SelectionChanged;
    }

    private sealed class StubDiagnostics : IDiagnosticsService
    {
        public event EventHandler<DiagnosticsChannelsChangedEventArgs>? ChannelsChanged;

        public event EventHandler<DiagnosticsChannelPublishedEventArgs>? DiagnosticsChannelPublished;

        public event EventHandler<DiagnosticsSnapshotPublishedEventArgs>? DiagnosticsSnapshotPublished;

        public event EventHandler<DiagnosticsPublishedEventArgs>? DiagnosticsPublished;

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(string? filePath, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DiagnosticsQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());

        public Task<IReadOnlyList<DiagnosticsDocumentSnapshot>> GetDiagnosticsSnapshotAsync(
            DiagnosticsQuery query,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DiagnosticsDocumentSnapshot>>(Array.Empty<DiagnosticsDocumentSnapshot>());

        public Task<IReadOnlyList<DiagnosticsChannelInfo>> GetChannelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DiagnosticsChannelInfo>>(Array.Empty<DiagnosticsChannelInfo>());

        public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;
    }

    private sealed class StubTerminalBridge : ITerminalBridge
    {
        public event EventHandler<TerminalChangedEventArgs>? TerminalCreated;
        public event EventHandler<TerminalChangedEventArgs>? TerminalClosed;
        public event EventHandler<ActiveTerminalChangedEventArgs>? ActiveTerminalChanged;
        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;
        public event EventHandler<TerminalExitEventArgs>? TerminalExited;
        public event EventHandler<TerminalDimensionsChangedEventArgs>? TerminalDimensionsChanged;

        public Task<TerminalInfo> CreateAsync(TerminalCreateRequest request, CancellationToken ct)
            => Task.FromResult(new TerminalInfo(Guid.NewGuid(), request.Title ?? "terminal"));

        public Task SendTextAsync(Guid terminalId, string text, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<TerminalInfo>> GetTerminalsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TerminalInfo>>(Array.Empty<TerminalInfo>());

        public Task<Guid?> GetActiveTerminalIdAsync(CancellationToken ct)
            => Task.FromResult<Guid?>(null);

        public Task<bool> CloseAsync(Guid terminalId, CancellationToken ct)
            => Task.FromResult(false);

        public Task<TaskExecutionResult> RunTaskAsync(TaskExecutionRequest request, CancellationToken ct)
            => Task.FromResult(new TaskExecutionResult(
                request.TaskId,
                0,
                Array.Empty<string>(),
                Array.Empty<TaskProblemMatch>()));
    }

    private sealed class StubExtensionLogger : IExtensionLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class RecordingCommands : ICommands
    {
        public string? LastCommandId { get; private set; }
        public IReadOnlyList<object?>? LastArguments { get; private set; }

        public IDisposable Register(string commandId, Func<CommandContext, Task> handler)
        {
            return new Registration();
        }

        public Task ExecuteAsync(string commandId, IReadOnlyList<object?>? args, CancellationToken cancellationToken)
        {
            LastCommandId = commandId;
            LastArguments = args;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetCommandsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        private sealed class Registration : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class StubWorkspaceService : IWorkspaceService
    {
        public Task<WorkspaceModel> LoadSolutionAsync(string solutionPath, CancellationToken ct = default)
        {
            return Task.FromResult(new WorkspaceModel
            {
                Projects = Array.Empty<ProjectModel>()
            });
        }

        public Task<WorkspaceModel> LoadProjectAsync(string projectPath, CancellationToken ct = default)
        {
            return Task.FromResult(new WorkspaceModel
            {
                Projects = Array.Empty<ProjectModel>()
            });
        }

        public WorkspaceModel CreateStandaloneWorkspace(string xamlFilePath)
        {
            return new WorkspaceModel
            {
                Projects = Array.Empty<ProjectModel>()
            };
        }
    }
}

#pragma warning restore CS0067
