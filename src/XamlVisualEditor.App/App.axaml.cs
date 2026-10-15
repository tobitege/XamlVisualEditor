using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions.Debugging;
using XamlVisualEditor.Core.Logging;
using XamlVisualEditor.CSharp.Language;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using AcpExtensionEntry = XamlVisualEditor.AcpExtension.AcpExtension;
using GitExtensionEntry = XamlVisualEditor.GitExtension.GitExtension;
using TfvcExtensionEntry = XamlVisualEditor.TfvcExtension.TfvcExtension;
using IdeBridgeExtensionEntry = XamlVisualEditor.IdeBridgeExtension.IdeBridgeExtension;
using McpExtensionEntry = XamlVisualEditor.McpExtension.McpExtension;
using VscodeCompatExtensionEntry = XamlVisualEditor.VscodeCompatExtension.VscodeCompatExtension;
using XamlVisualEditor.Language;
using XamlVisualEditor.Acp;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.Lsp;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Intellisense;
using XamlVisualEditor.Xaml.Language;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using XamlVisualEditor.Terminal;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using DotNetTemplatesExtensionEntry = XamlVisualEditor.DotNetTemplatesExtension.DotNetTemplatesExtension;
using DapDebuggingExtensionEntry = XamlVisualEditor.Debugging.DapExtension.DapDebuggingExtension;
using DotNetSdkDebuggingExtensionEntry = XamlVisualEditor.Debugging.DotNetSdkExtension.DotNetSdkDebuggingExtension;
using ExtensionSystemIconServiceEntry = XamlVisualEditor.FileExplorerExtension.ExtensionSystemIconService;
using FileExplorerExtensionEntry = XamlVisualEditor.FileExplorerExtension.FileExplorerExtension;
using SolutionExplorerExtensionEntry = XamlVisualEditor.SolutionExplorerExtension.SolutionExplorerExtension;
using ToolboxExtensionEntry = XamlVisualEditor.ToolboxExtension.ToolboxExtension;
using TreeInspectorExtensionEntry = XamlVisualEditor.TreeInspectorExtension.TreeInspectorExtension;
using NavigationExtensionEntry = XamlVisualEditor.NavigationExtension.NavigationExtension;
using AnimationEditorExtensionEntry = XamlVisualEditor.AnimationEditorExtension.AnimationEditorExtension;
using CollaborationExtensionEntry = XamlVisualEditor.CollaborationExtension.CollaborationExtension;
using DebugSettingsExtensionEntry = XamlVisualEditor.DebugSettingsExtension.DebugSettingsExtension;
using LspSettingsExtensionEntry = XamlVisualEditor.LspSettingsExtension.LspSettingsExtension;
using PropertyEditorExtensionEntry = XamlVisualEditor.PropertyEditorExtension.PropertyEditorExtension;
using OutputExtensionEntry = XamlVisualEditor.OutputExtension.OutputExtension;
using XamlEditorExtensionEntry = XamlVisualEditor.XamlEditorExtension.XamlEditorExtension;
using WorkspaceExtensionEntry = XamlVisualEditor.WorkspaceExtension.WorkspaceExtension;
using TextEditingExtensionEntry = XamlVisualEditor.TextEditingExtension.TextEditingExtension;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.App;

/// <summary>
/// Application entry point and composition root.
/// </summary>
public sealed class App : Application
{
    private readonly EditorOptionsStore _editorOptions = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XamlVisualEditor", "editor-options.json"));
    /// <summary>
    /// Gets the service provider for DI.
    /// </summary>
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(_editorOptions.Current.Language);
        XamlVisualEditor.Core.Localization.UiStrings.Culture = culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        AvaloniaXamlLoader.Load(this);
        LocalizedResources.Apply(this);
        Views.DocumentTabContextMenus.Extend(this);
        DialogAppearance.Initialize();
        DataGridColumnSizing.Initialize();
        DockSplitterAppearance.Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Build the DI container
        Log.Information("Configuring application services");
        ServiceCollection services = new();
        ConfigureServices(services, _editorOptions);
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Begun before the main view model exists, so the status bar starts with the startup text instead of "Ready".
            IDisposable startupActivity = Services.GetRequiredService<IBackgroundActivities>().Begin(UiStrings.StartingStatus);
            Log.Information("Creating the main window");
            MainWindowViewModel mainVm = Services.GetRequiredService<MainWindowViewModel>();
            Resources[TextEditorViewHost.RESOURCE_KEY] = Services.GetRequiredService<TextEditorViewHost>();
            MainWindow mainWindow = new(mainVm);
            desktop.MainWindow = mainWindow;

            MainWindowProvider windowProvider = Services.GetRequiredService<MainWindowProvider>();
            windowProvider.MainWindow = mainWindow;
            string? startupPath = StartupArgs.GetWorkspacePath(desktop.Args);
            CancellationTokenSource startupCancellation = new();
            CancellationToken startupToken = startupCancellation.Token;
            IServiceProvider serviceProvider = Services;

            desktop.Exit += (_, _) =>
            {
                startupCancellation.Cancel();
                mainWindow.DataContext = null;
                mainWindow.Content = null;
                (serviceProvider as IDisposable)?.Dispose();
                startupCancellation.Dispose();
            };

            Dispatcher.UIThread.Post(() =>
                _ = InitializeExtensionsAsync(serviceProvider, mainVm, startupPath, startupActivity, startupToken));
            Log.Information("Main window created; extension activation scheduled");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeExtensionsAsync(
        IServiceProvider services,
        MainWindowViewModel mainVm,
        string? startupPath,
        IDisposable startupActivity,
        CancellationToken cancellationToken)
    {
        using IDisposable activity = startupActivity;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log.Information("Activating built-in extensions");
            BuiltInExtensionHost extensionHost = services.GetRequiredService<BuiltInExtensionHost>();
            await extensionHost.ActivateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await services.GetRequiredService<IExtensionManager>().GetInstalledAsync(cancellationToken);

            if (services.GetService<IWindow>() is AppWindow appWindow)
            {
                appWindow.SyncStatusBarItems();
            }

            Log.Information("Built-in extensions activated; editor ready");
#if DEBUG
            services.GetRequiredService<CdpDiagnostics>().StartFromEnvironment();
#endif
            if (!string.IsNullOrWhiteSpace(startupPath))
            {
                await mainVm.OpenFileAsync(startupPath);
            }
            else if (services.GetRequiredService<EditorOptionsStore>().Current.RestoreLastWorkspace)
            {
                await mainVm.RestoreLastWorkspaceAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("Application startup canceled during shutdown");
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Application startup failed");
            if (!cancellationToken.IsCancellationRequested)
            {
                await services.GetRequiredService<IWindow>().ShowErrorMessageAsync(
                    string.Format(UiStrings.ApplicationStartupFailed, (object?)(exception.Message)), cancellationToken);
            }
        }
    }

    private static void ConfigureServices(ServiceCollection services, EditorOptionsStore editorOptions)
    {
        services.AddSingleton(editorOptions);
#if DEBUG
        services.AddSingleton<CdpDiagnostics>();
#endif
        OutputLogSinkAccessor outputLogSinkAccessor = new();
        FileLoggingSetup.ReplaceGlobalLogger(
            FileLoggingSetup.CreateLogger(new OutputPanelLogSink(outputLogSinkAccessor)));

        services.AddSingleton<IOutputLogSinkAccessor>(outputLogSinkAccessor);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: true);
        });

        // Core services (Singleton — thread-safe, shared)
        services.AddSingleton<IXamlParsingService, XamlParsingService>();
        services.AddSingleton<IXamlSerializationService, XamlSerializationService>();
        services.AddSingleton<ITypeMetadataService, TypeMetadataService>();
        services.AddSingleton(_ => new MetadataControlCatalog(TypeMetadataService.ReadControlTypes));
        services.AddSingleton<IControlCatalog>(provider => provider.GetRequiredService<MetadataControlCatalog>());
        services.AddSingleton<IControlCatalogUpdater>(provider => provider.GetRequiredService<MetadataControlCatalog>());
        services.AddSingleton<IClipboardService, DesktopClipboardService>();
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<IDotNetCli, DotNetCliRunner>();
        services.AddSingleton<IDotNetTemplateService, DotNetTemplateService>();
        services.AddSingleton<IGitService, GitService>();
        services.AddSingleton<IAnimationPreviewService, AnimationPreviewService>();
        services.AddSingleton<IDebugToolInstaller, DebugToolInstaller>();
        services.AddSingleton<IPtyProvider>(_ => PtyProviderFactory.CreateDefault());
        services.AddSingleton<ITerminalEmulatorFactory, ManagedTerminalEmulatorFactory>();
        services.AddSingleton<ITerminalService, TerminalService>();
        services.AddSingleton<IAcpAgentHostFactory, AcpAgentHostFactory>();
        services.AddSingleton<IAcpSettings, AcpSettings>();
        services.AddSingleton<IAcpService, AcpService>();
        services.AddSingleton<IAcpProfileStore, AcpProfileStore>();
        services.AddSingleton<ISecretStore, OsSecretStore>();
        services.AddSingleton<IAcpOAuthDeviceFlowService, AcpOAuthDeviceFlowService>();
        services.AddSingleton<IDebuggerServiceRegistry, DebuggerServiceRegistry>();

        services.AddSingleton<WorkspaceInfoService>();
        services.AddSingleton<IWorkspaceInfo>(sp => sp.GetRequiredService<WorkspaceInfoService>());
        services.AddSingleton<IWorkspaceInfoUpdater>(sp => sp.GetRequiredService<WorkspaceInfoService>());
        services.AddSingleton<IWorkspace, FileSystemWorkspace>();
        services.AddSingleton<IWindow, AppWindow>();
        services.AddSingleton<ISettings, InMemorySettingsStore>();
        services.AddSingleton<ISystemIconService, ExtensionSystemIconServiceEntry>();
        services.AddSingleton<IPropertyEditorRegistry, PropertyEditorRegistry>();
        services.AddSingleton<IWorkspaceCommands>(sp => sp.GetRequiredService<MainWindowViewModel>());
        services.AddSingleton<MainWindowProvider>();
        services.AddSingleton<IFolderPicker, AppFolderPicker>();
        services.AddSingleton<IDialogHost, AppDialogHost>();
        services.AddSingleton<IWorkspaceHost, AppWorkspaceHost>();
        services.AddSingleton<IUserLayoutStore>(_ => UserLayoutStore.CreateDefault());
        services.AddSingleton<BackgroundActivityTracker>();
        services.AddSingleton<IBackgroundActivities>(sp => sp.GetRequiredService<BackgroundActivityTracker>());
        services.AddSingleton<IBackgroundActivityMonitor>(sp => sp.GetRequiredService<BackgroundActivityTracker>());
        services.AddSingleton<DocumentOpenInterceptorRegistry>();
        services.AddSingleton<IDocumentOpenInterceptors>(sp => sp.GetRequiredService<DocumentOpenInterceptorRegistry>());
        services.AddSingleton<IDocumentOpenResolver>(sp => sp.GetRequiredService<DocumentOpenInterceptorRegistry>());

        // LSP services
        services.AddSingleton<ILspSettingsStore, LspSettingsStore>();
        services.AddSingleton<ILspSettings>(sp => new LspSettings(
            sp.GetRequiredService<ILspSettingsStore>()));
        services.AddSingleton<ILanguageServiceRouter>(sp => new LspLanguageServiceRouter(
            sp.GetRequiredService<ILspSettings>().Servers,
            sp.GetService<ILoggerFactory>()));
        services.AddSingleton<ILanguageIntellisenseService, LspLanguageIntellisenseService>();

        // Language services
        services.AddSingleton<ILanguageIntellisenseService, CSharpLanguageService>();
        services.AddSingleton<ILanguageIntellisenseService, XamlLanguageService>();
        services.AddSingleton<ExtensionLanguageServiceRegistry>();
        services.AddSingleton<ILanguageIntellisenseService, ExtensionLanguageIntellisenseService>();
        services.AddSingleton<ILanguageIntellisenseRegistry, LanguageServiceRegistry>();

        // Transient services (stateless/lightweight)
        services.AddTransient<CompletionProviderRegistry>(_ => CompletionProviderRegistry.CreateDefault());
        services.AddTransient<AstNodeMap>();

        // Extension services
        services.AddSingleton<ICommands, CommandRegistry>();
        services.AddSingleton<ICommandMetadataRegistry, CommandMetadataRegistry>();
        services.AddSingleton<IExtensionContributionRegistry, ExtensionContributionRegistry>();
        services.AddSingleton<IExtensionViewRegistry, ExtensionViewRegistry>();
        services.AddSingleton<IViews>(sp => sp.GetRequiredService<IExtensionViewRegistry>());
        services.AddSingleton<IExtensionLanguageServices>(sp => sp.GetRequiredService<ExtensionLanguageServiceRegistry>());
        services.AddSingleton<ILanguageNavigationService, LanguageNavigationServiceAdapter>();
        services.AddSingleton<INavigationHistoryService, NavigationHistoryServiceAdapter>();
        services.AddSingleton<IAnimationEditorHost, AnimationEditorHostAdapter>();
        services.AddSingleton<CollaborationPanelHostAdapter>();
        services.AddSingleton<ICollaborationHost>(sp => sp.GetRequiredService<CollaborationPanelHostAdapter>());
        services.AddSingleton<ICollaborationPanelHost>(sp => sp.GetRequiredService<CollaborationPanelHostAdapter>());
        services.AddSingleton<ISolutionExplorerPanelHost, SolutionExplorerPanelHostAdapter>();
        services.AddSingleton<IDebugSettingsHost, DebugSettingsHostAdapter>();
        services.AddSingleton<ILspSettingsHost, LspSettingsHostAdapter>();
        services.AddSingleton<IEditorServices, EditorServicesAdapter>();
        services.AddSingleton(sp =>
        {
            MainWindowViewModel mainViewModel = sp.GetRequiredService<MainWindowViewModel>();
            IObservable<object?> activeDocument = mainViewModel.WhenAnyValue(x => x.ActiveTextEditorDocument);
            return new TextEditorViewHost(mainViewModel.EditorContextMenuItems, activeDocument, sp.GetRequiredService<IContextKeys>());
        });
        services.AddSingleton<ITextEditorViewHost>(sp => sp.GetRequiredService<TextEditorViewHost>());
        services.AddSingleton<IContextKeys, ContextKeyRegistry>();
        services.AddSingleton<IDesignerHost, ShellDesignerHost>();
        services.AddSingleton<IWorkspaceModel, WorkspaceModelAdapter>();
        services.AddSingleton<IDiagnosticsService, DiagnosticsServiceAdapter>();
        services.AddSingleton<ITerminalBridge, TerminalBridgeAdapter>();
        services.AddSingleton<IExtensionViewHost, ExtensionViewHostAdapter>();
        services.AddSingleton<ExtensionPackageLoader>();
        services.AddSingleton<IExtensionPackageStore>(sp => new ExtensionPackageStore(
            ExtensionPackagePaths.GetInstalledRoot(),
            sp.GetRequiredService<ExtensionPackageLoader>(),
            sp.GetService<ILogger<ExtensionPackageStore>>()));
        services.AddSingleton<IExtensionPackageCatalog>(sp => new LocalExtensionPackageCatalog(
            ExtensionPackagePaths.GetCatalogRoot(),
            sp.GetRequiredService<ExtensionPackageLoader>()));
        services.AddSingleton<IExtensionStateStore>(_ => new FileExtensionStateStore(
            ExtensionPackagePaths.GetStateFilePath()));
        services.AddSingleton<IExtensionUpdateService, ExtensionUpdateService>();
        services.AddSingleton<IExtensionManager, ExtensionManager>();
        services.AddSingleton<EditorThemeService>();
        services.AddSingleton<IEditorThemeService>(sp => sp.GetRequiredService<EditorThemeService>());
        services.AddSingleton<Func<string, IExtensionStorage>>(_ =>
            extensionId => new FileExtensionStorage(extensionId));
        services.AddSingleton<BuiltInExtensionHost>();
        services.AddSingleton<IXveExtension, AcpExtensionEntry>();
        services.AddSingleton<IXveExtension, GitExtensionEntry>();
        services.AddSingleton<IXveExtension, TfvcExtensionEntry>();
        services.AddSingleton<IXveExtension, IdeBridgeExtensionEntry>();
        services.AddSingleton<IXveExtension, McpExtensionEntry>();
        services.AddSingleton<IXveExtension, VscodeCompatExtensionEntry>();
        services.AddSingleton<IXveExtension, DotNetTemplatesExtensionEntry>();
        services.AddSingleton<IXveExtension, FileExplorerExtensionEntry>();
        services.AddSingleton<IXveExtension, SolutionExplorerExtensionEntry>();
        services.AddSingleton<IXveExtension, ToolboxExtensionEntry>();
        services.AddSingleton<IXveExtension, PropertyEditorExtensionEntry>();
        services.AddSingleton<IXveExtension, OutputExtensionEntry>();
        services.AddSingleton<IXveExtension, NavigationExtensionEntry>();
        services.AddSingleton<IXveExtension, XamlEditorExtensionEntry>();
        services.AddSingleton<IXveExtension, TreeInspectorExtensionEntry>();
        services.AddSingleton<IXveExtension, AnimationEditorExtensionEntry>();
        services.AddSingleton<IXveExtension, CollaborationExtensionEntry>();
        services.AddSingleton<IXveExtension, DebugSettingsExtensionEntry>();
        services.AddSingleton<IXveExtension, LspSettingsExtensionEntry>();
        services.AddSingleton<IXveExtension, WorkspaceExtensionEntry>();
        services.AddSingleton<IXveExtension, TextEditingExtensionEntry>();
        services.AddSingleton<IXveExtension, DapDebuggingExtensionEntry>();
        services.AddSingleton<IXveExtension, DotNetSdkDebuggingExtensionEntry>();

        // ViewModels (Singleton for shell-level, Transient for per-document)
        services.AddSingleton(sp =>
        {
            string profileDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XamlVisualEditor");
            System.IO.Directory.CreateDirectory(profileDirectory);
            string recentFilesPath = System.IO.Path.Combine(profileDirectory, "recent-files.json");
            return ActivatorUtilities.CreateInstance<MainWindowViewModel>(sp, recentFilesPath);
        });
    }
}
