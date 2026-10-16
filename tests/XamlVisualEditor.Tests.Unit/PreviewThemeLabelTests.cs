using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Tests.Unit.Extensions;
using XamlVisualEditor.Workspace;
using SampleThemeProvider = XamlVisualEditor.TestControls.SampleThemeProvider;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

[Collection(nameof(UiLocalizationCollection))]
public sealed class PreviewThemeLabelTests : IDisposable
{
    private const string ProviderId = "sample.palette";
    private const string OtherProviderId = "other.palette";
    private const string HarborPending = "harbor (waiting for the preview)";
    private const string HarborUnavailable = "Unavailable: harbor";
    private const string Xaml = "<UserControl xmlns=\"https://github.com/avaloniaui\" />";
    private static readonly DesignerThemeSelection Harbor = new(ProviderId, "harbor");
    private static readonly DesignerThemeSelection Clay = new(ProviderId, "clay");
    private static readonly WorkspaceModel s_emptyWorkspace = new() { Projects = Array.Empty<ProjectModel>() };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "XamlVisualEditor-PreviewThemeLabel-" + Guid.NewGuid().ToString("N"));

    public PreviewThemeLabelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [AvaloniaFact]
    public void SelectionBeforeAnyCatalogShowsThePendingLabelWithoutTheProviderId()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("Pending.axaml");

        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);

        DesignerThemeChoice selected = Assert.IsType<DesignerThemeChoice>(document.DesignSurface.SelectedPreviewTheme);
        Assert.Equal(Harbor, selected.Selection);
        Assert.Equal("harbor (waiting for the preview)", selected.DisplayName);
        Assert.DoesNotContain(ProviderId, selected.DisplayName);
    }

    [AvaloniaFact]
    public async Task CatalogWithTheSelectedThemeReplacesThePendingLabelWithItsName()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("Listed.axaml");
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);
        using PreviewerTcpSession session = await ConnectAsync(document);

        Assert.True(session.TryReceiveThemeCatalog(Message(document, new DesignerTheme("harbor", "Harbor", false))));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal("Harbor", document.DesignSurface.SelectedPreviewTheme?.DisplayName);
        Assert.Equal(Harbor, document.DesignSurface.SelectedPreviewTheme?.Selection);
        Assert.DoesNotContain(document.DesignSurface.PreviewThemes, choice => choice.DisplayName.Contains("waiting", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task CatalogWithoutTheSelectedThemeShowsTheUnavailableLabel()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("Missing.axaml");
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);
        using PreviewerTcpSession session = await ConnectAsync(document);

        Assert.True(session.TryReceiveThemeCatalog(Message(document, new DesignerTheme("clay", "Clay", false))));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal("Unavailable: harbor", document.DesignSurface.SelectedPreviewTheme?.DisplayName);
        Assert.Equal(Harbor, document.DesignSurface.SelectedPreviewTheme?.Selection);
    }

    [AvaloniaFact(Timeout = 60000)]
    public async Task PreviewerExitShowsTheUnavailableLabelAcrossTabSwitchesUntilANewSessionArrives()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using CultureScope culture = new("en");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        DesignerDocumentViewModel document = await OpenAsync(main, "Exited.axaml", cancellationToken);
        DesignerDocumentViewModel other = await OpenAsync(main, "Other.axaml", cancellationToken);
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);
        using Process host = StartHostThatEndsWithItsInput();
        main.PreviewerLaunchService.AddRunningInstance(document.FilePath, s_emptyWorkspace, null, host,
            new PreviewerTcpSession(document.FilePath, log: null));
        main.ApplyPreviewerUpdateResult(document, new PreviewerLaunchResult(true, null, host));
        Assert.Equal(PreviewerState.Running, document.PreviewerState);
        Assert.Equal(HarborPending, document.DesignSurface.SelectedPreviewTheme?.DisplayName);
        List<string> labels = RecordLabels(document);
        Task exited = WaitForStateAsync(document, PreviewerState.Exited);

        // The host exits with code 0 once its input closes, as a previewer process does when it ends by itself.
        host.StandardInput.Close();
        await exited.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.Equal(new[] { HarborUnavailable }, labels);

        // Without UseExternalPreviewer no previewer restarts, so the tab switches leave the document without a session.
        Assert.False(document.UseExternalPreviewer);
        main.ActiveDocument = document;
        main.ActiveDocument = other;
        main.ActiveDocument = document;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Null(document.PreviewerSession);
        Assert.Equal(PreviewerState.Exited, document.PreviewerState);
        Assert.Equal(new[] { HarborUnavailable }, labels);

        PreviewerTcpSession restarted = new(document.FilePath, log: null);
        main.PreviewerLaunchService.AddRunningInstance(document.FilePath, s_emptyWorkspace, null, null, restarted);
        main.ApplyPreviewerUpdateResult(document, new PreviewerLaunchResult(true, null, null));

        Assert.Same(restarted, document.PreviewerSession);
        Assert.Equal(PreviewerState.Running, document.PreviewerState);
        Assert.Equal(new[] { HarborUnavailable, HarborPending }, labels);
        Assert.Equal(HarborPending, Assert.Single(document.DesignSurface.PreviewThemes, choice => choice.Selection == Harbor).DisplayName);
    }

    [AvaloniaFact(Timeout = 60000)]
    public async Task ThemeRestartKeepsTheWaitingLabelWhileTheReplacedSessionCloses()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using CultureScope culture = new("en");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        DesignerDocumentViewModel document = await OpenAsync(main, "Restart.axaml", cancellationToken);
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);
        main.PreviewerLaunchService.AddRunningInstance(document.FilePath, s_emptyWorkspace, null, null,
            new PreviewerTcpSession(document.FilePath, log: null));
        main.ApplyPreviewerUpdateResult(document, new PreviewerLaunchResult(true, null, null));
        List<string> labels = RecordLabels(document);

        // A theme change ends the old host and registers the new one before the update result reaches the document,
        // which still holds the closed session until then.
        main.PreviewerLaunchService.StopPreviewer(document.FilePath);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        PreviewerTcpSession restarted = new(document.FilePath, log: null);
        main.PreviewerLaunchService.AddRunningInstance(document.FilePath, s_emptyWorkspace, null, null, restarted);
        main.ApplyPreviewerUpdateResult(document, new PreviewerLaunchResult(true, null, null));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Same(restarted, document.PreviewerSession);
        Assert.Equal(PreviewerState.Running, document.PreviewerState);
        Assert.Empty(labels);
        Assert.Equal(HarborPending, document.DesignSurface.SelectedPreviewTheme?.DisplayName);
    }

    [AvaloniaFact]
    public void FailedPreviewerStartShowsTheUnavailableLabelUntilTheNextStart()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("StartFailed.axaml");
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);

        document.PreviewerState = PreviewerState.Failed;
        Assert.Equal(HarborUnavailable, document.DesignSurface.SelectedPreviewTheme?.DisplayName);

        document.PreviewerState = PreviewerState.Starting;
        Assert.Equal(HarborPending, document.DesignSurface.SelectedPreviewTheme?.DisplayName);
    }

    [AvaloniaFact(Timeout = 60000)]
    public async Task CanceledPreviewerStartShowsTheUnavailableLabelAndTheCancelStatusUntilTheNextStart()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using CultureScope culture = new("en");
        string projectPath = Path.Combine(_root, "Views.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project />", cancellationToken);
        string xamlPath = Path.Combine(_root, "Canceled.axaml");
        await File.WriteAllTextAsync(xamlPath, Xaml, cancellationToken);
        WorkspaceModel workspace = new()
        {
            Projects = new[]
            {
                new ProjectModel
                {
                    Name = "Views", ProjectPath = projectPath, OutputAssemblyPath = Path.Combine(_root, "missing", "Views.dll"),
                    XamlFiles = new[] { new XamlFileModel { FilePath = xamlPath, RelativePath = "Canceled.axaml" } },
                    Files = Array.Empty<ProjectFileModel>(), References = Array.Empty<AssemblyReference>()
                }
            }
        };
        using MainWindowViewModel main = new(
            workspaceService: new FixedWorkspaceService(workspace),
            metadataService: new TypeMetadataService(),
            recentFilesPath: Path.Combine(_root, "recent.json"),
            workspaceSessions: new WorkspaceSessionStore(Path.Combine(_root, "sessions.json")));
        int prompts = 0;
        using IDisposable trust = main.PreviewerTrustInteraction.RegisterHandler(context =>
        {
            prompts++;
            context.SetOutput(PreviewerTrustDecision.Cancel);
        });
        await main.OpenFileAsync(projectPath).WaitAsync(cancellationToken);
        Assert.True(main.HasWorkspace);
        await main.OpenFileAsync(xamlPath).WaitAsync(cancellationToken);
        DesignerDocumentViewModel document = Assert.IsType<DesignerDocumentViewModel>(main.ActiveDocument);

        // A document of a project starts its previewer once loaded; declining the trust prompt cancels that start.
        await WaitForCanceledStartAsync(document, cancellationToken);
        Assert.Equal(1, prompts);
        DesignerDocumentViewModel other = await OpenAsync(main, "Other.axaml", cancellationToken);
        await WaitForCanceledStartAsync(other, cancellationToken);
        Assert.Equal(2, prompts);

        // Activating the document again does not ask again.
        main.ActiveDocument = document;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(2, prompts);
        Assert.Equal(PreviewerState.Canceled, document.PreviewerState);
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);
        PreviewerSurfaceControl surface = new() { Document = document, Width = 200, Height = 100 };
        Window window = new() { Content = surface, Width = 300, Height = 200 };
        try
        {
            window.Show();

            Assert.Equal(HarborUnavailable, document.DesignSurface.SelectedPreviewTheme?.DisplayName);
            Assert.Equal(UiStrings.PreviewerStartCanceled, surface.StatusText);

            // Turning the previewer off and on again requests a new start, which asks again.
            document.UseExternalPreviewer = false;
            Task restarted = WaitForStateAsync(document, PreviewerState.Starting);
            document.UseExternalPreviewer = true;
            await restarted.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            await WaitForCanceledStartAsync(document, cancellationToken);
            Assert.Equal(3, prompts);
            Assert.Equal(HarborUnavailable, document.DesignSurface.SelectedPreviewTheme?.DisplayName);

            document.PreviewerState = PreviewerState.Starting;
            Assert.Equal(HarborPending, document.DesignSurface.SelectedPreviewTheme?.DisplayName);
            Assert.Equal(UiStrings.PreviewStatusWaiting, surface.StatusText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SecondSelectionWithoutACatalogReplacesThePendingEntry()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("TwoPending.axaml");

        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);
        document.SetPreviewThemeSelection(Clay, activatePreviewer: false);

        Assert.Equal(new[] { "Project default", "clay (waiting for the preview)" },
            document.DesignSurface.PreviewThemes.Select(choice => choice.DisplayName));
        Assert.Equal(Clay, document.DesignSurface.SelectedPreviewTheme?.Selection);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectingTheProjectDefaultRemovesThePendingEntrySoNoStateChangeRenamesIt(bool throughTheSelector)
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("PendingRemoved.axaml");
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);

        if (throughTheSelector) document.DesignSurface.SelectedPreviewTheme = DesignerThemeChoice.ProjectDefault;
        else document.SetPreviewThemeSelection(null);
        document.PreviewerState = PreviewerState.Failed;

        Assert.Null(document.PreviewThemeRequest!.Selection);
        Assert.Equal(new[] { DesignerThemeChoice.ProjectDefault }, document.DesignSurface.PreviewThemes);
        Assert.Same(DesignerThemeChoice.ProjectDefault, document.DesignSurface.SelectedPreviewTheme);
    }

    [AvaloniaFact]
    public async Task SelectingACatalogedThemeRemovesThePendingEntry()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("CatalogedPick.axaml");
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);
        using PreviewerTcpSession session = await ConnectAsync(document);
        Assert.True(session.TryReceiveThemeCatalog(Message(document, new DesignerTheme("harbor", "Harbor", true),
            new DesignerTheme("clay", "Clay", false))));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        document.SetPreviewThemeSelection(new DesignerThemeSelection(ProviderId, "missing"), activatePreviewer: false);
        Assert.Equal("missing (waiting for the preview)", document.DesignSurface.SelectedPreviewTheme?.DisplayName);

        document.DesignSurface.SelectedPreviewTheme = document.DesignSurface.PreviewThemes.Single(choice => choice.Selection == Clay);

        Assert.Equal(Clay, document.PreviewThemeRequest!.Selection);
        Assert.Equal(new[] { "Project default", "Clay", "Harbor" }, document.DesignSurface.PreviewThemes.Select(choice => choice.DisplayName));
    }

    [AvaloniaTheory]
    [InlineData(new[] { ProviderId }, HarborPending)]
    [InlineData(new[] { ProviderId, OtherProviderId }, "Sample: harbor (waiting for the preview)")]
    [InlineData(new[] { OtherProviderId }, "sample.palette: harbor (waiting for the preview)")]
    public void PendingLabelNamesTheProviderOnlyWhenAnotherProviderCouldOfferTheSameId(string[] providerIds, string expected)
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("Providers.axaml");
        document.SetPreviewThemeProviders(providerIds.Select(Registration).ToArray());

        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);

        Assert.Equal(expected, document.DesignSurface.SelectedPreviewTheme?.DisplayName);
    }

    [AvaloniaFact]
    public void RegisteringASecondProviderRelabelsThePendingSelectionWithItsProvider()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("LateProviders.axaml");
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);
        Assert.Equal(HarborPending, document.DesignSurface.SelectedPreviewTheme?.DisplayName);

        document.SetPreviewThemeProviders(new[] { Registration(ProviderId), Registration(OtherProviderId) });
        Assert.Equal("Sample: harbor (waiting for the preview)", document.DesignSurface.SelectedPreviewTheme?.DisplayName);

        document.PreviewerState = PreviewerState.Exited;
        Assert.Equal("Unavailable: Sample: harbor", document.DesignSurface.SelectedPreviewTheme?.DisplayName);
        Assert.Single(document.DesignSurface.PreviewThemes, choice => choice.Selection == Harbor);
    }

    [Fact]
    public void ThemeChoiceRejectsAMissingDisplayName()
    {
        Assert.Throws<ArgumentNullException>(() => new DesignerThemeChoice(Harbor, null!));
        Assert.Throws<ArgumentNullException>(() => new DesignerThemeChoice(null, null!));
        Assert.Throws<ArgumentNullException>(() => new DesignerThemeChoice(Harbor, "Harbor") with { DisplayName = null! });
    }

    [Fact]
    public void ThemeChoiceKeepsWithExpressionsAndDeconstructionOfThePositionalRecord()
    {
        DesignerThemeChoice renamed = new DesignerThemeChoice(Harbor, "Harbor") with { DisplayName = "Renamed" };
        (DesignerThemeSelection? selection, string displayName) = renamed;
        DesignerThemeChoice named = DesignerThemeChoice.ProjectDefault with { DisplayName = "Fixed" };

        Assert.Equal(Harbor, selection);
        Assert.Equal("Renamed", displayName);
        Assert.Equal(new DesignerThemeChoice(Harbor, "Renamed"), renamed);
        Assert.Equal(new DesignerThemeChoice(Clay, "Clay"), new DesignerThemeChoice(null, "Clay") { Selection = Clay });
        Assert.Equal("Fixed", named.DisplayName);
        Assert.Null(named.Selection);
        Assert.NotEqual(DesignerThemeChoice.ProjectDefault, named);
    }

    [AvaloniaTheory]
    [InlineData("en", "Project default")]
    [InlineData("de", "Projektstandard")]
    public void ProjectDefaultLabelFollowsTheUiCulture(string language, string expected)
    {
        using CultureScope culture = new(language);
        using DesignerDocumentViewModel document = new("ProjectDefault.axaml");

        Assert.Equal(expected, DesignerThemeChoice.ProjectDefault.DisplayName);
        Assert.Same(DesignerThemeChoice.ProjectDefault, document.DesignSurface.SelectedPreviewTheme);
        Assert.Equal(expected, document.DesignSurface.PreviewThemes[0].DisplayName);
        Assert.NotEqual(DesignerThemeChoice.ProjectDefault, new DesignerThemeChoice(null, expected));
    }

    private async Task<DesignerDocumentViewModel> OpenAsync(MainWindowViewModel main, string fileName, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_root, fileName);
        await File.WriteAllTextAsync(path, Xaml, cancellationToken);
        await main.OpenFileAsync(path).WaitAsync(cancellationToken);
        return Assert.IsType<DesignerDocumentViewModel>(main.ActiveDocument);
    }

    private static Process StartHostThatEndsWithItsInput()
    {
        ProcessStartInfo startInfo = new("sort")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The host stand-in did not start.");
    }

    private static DesignerThemeProviderRegistration Registration(string providerId) =>
        new(providerId, providerId == ProviderId ? "Sample" : "Other", typeof(SampleThemeProvider).Assembly.Location,
            typeof(SampleThemeProvider).FullName!);

    // Records the label of every selected entry, including an entry relabeled in place.
    private static List<string> RecordLabels(DesignerDocumentViewModel document)
    {
        List<string> labels = new();
        document.DesignSurface.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DesignSurfaceViewModel.SelectedPreviewTheme)
                && document.DesignSurface.SelectedPreviewTheme is { } choice) labels.Add(choice.DisplayName);
        };
        return labels;
    }

    private static Task WaitForStateAsync(DesignerDocumentViewModel document, PreviewerState state)
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DesignerDocumentViewModel.PreviewerState) || document.PreviewerState != state) return;
            document.PropertyChanged -= OnPropertyChanged;
            reached.TrySetResult();
        }
        document.PropertyChanged += OnPropertyChanged;
        return reached.Task;
    }

    private static async Task WaitForCanceledStartAsync(DesignerDocumentViewModel document, CancellationToken cancellationToken)
    {
        Task canceled = WaitForStateAsync(document, PreviewerState.Canceled);
        if (document.PreviewerState != PreviewerState.Canceled) await canceled.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    private static async Task<PreviewerTcpSession> ConnectAsync(DesignerDocumentViewModel document)
    {
        PreviewerTcpSession session = new(document.FilePath, null);
        document.PreviewerSession = session;
        await session.SendUpdateXamlAsync("<Canvas xmlns=\"https://github.com/avaloniaui\" />", typeof(SampleThemeProvider).Assembly.Location,
            "/" + document.FilePath, 160, 80, themeRequestId: document.PreviewThemeRequest!.RequestId);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return session;
    }

    private static string Message(DesignerDocumentViewModel document, params DesignerTheme[] themes) =>
        DesignerThemeProtocol.CatalogPrefix + JsonSerializer.Serialize(new DesignerThemeCatalog(document.PreviewThemeRequest!.RequestId,
            new[] { new DesignerThemeProviderCatalog(ProviderId, "Sample", themes) }));

    private sealed class FixedWorkspaceService(WorkspaceModel workspace) : XamlVisualEditor.Core.Interfaces.IWorkspaceService
    {
        public Task<WorkspaceModel> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) =>
            Task.FromResult(workspace);

        public Task<WorkspaceModel> LoadProjectAsync(string projectPath, CancellationToken ct = default) =>
            Task.FromResult(workspace);

        public WorkspaceModel CreateStandaloneWorkspace(string xamlFilePath) => workspace;
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo? _previous = UiStrings.Culture;

        public CultureScope(string language) => UiStrings.Culture = CultureInfo.GetCultureInfo(language);

        public void Dispose() => UiStrings.Culture = _previous;
    }
}
