using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Xml.Linq;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls.DataGridFiltering;
using Avalonia.Controls.DataGridHierarchical;
using Avalonia.Controls.DataGridSearching;
using Avalonia.Controls.DataGridSorting;
using XamlVisualEditor.Core.Localization;
using AvOrientation = Avalonia.Layout.Orientation;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;
using Dock.Model.ReactiveUI.Controls;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.CodeEditor;
using XamlVisualEditor.Collaboration;
using XamlVisualEditor.Collaboration.UI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Designer.Adorners;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Designer.DragDrop;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Lsp;
using XamlVisualEditor.Sync;
using XamlVisualEditor.TreeView;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Intellisense;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Extensions.Debugging;
using XamlVisualEditor.Core.Logging;
using XamlVisualEditor.Terminal;
using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>
/// Common interface for editor documents.
/// </summary>
public interface IEditorDocumentViewModel : IDisposable
{
    string FilePath { get; }
    string FileName { get; }
    bool IsModified { get; }
    /// <summary>Gets whether the file on disk carries the read-only attribute.</summary>
    bool IsReadOnlyFile { get; }
    int CurrentLine { get; }
    int CurrentColumn { get; }
    ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }

    /// <summary>Saves the editor text even though the file changed on disk after it was read or last saved.</summary>
    System.Threading.Tasks.Task OverwriteFileAsync();

    /// <summary>Replaces the editor text with the current file content on disk.</summary>
    System.Threading.Tasks.Task ReloadFromDiskAsync();
}

/// <summary>
/// ViewModel for a XAML document tab (designer + code split view).
/// </summary>
public sealed partial class DesignerDocumentViewModel : ReactiveObject, IEditorDocumentViewModel
{
    private readonly CompositeDisposable _disposables = new();
    private readonly CancellationTokenSource _loadCancellation = new();
    public bool IsDisposed { get; private set; }
    [Reactive]
    public partial bool IsLoaded { get; private set; }
    [Reactive]
    public partial bool IsLoading { get; private set; }
    [Reactive]
    public partial string? LoadingStatus { get; private set; }
    private SyncSource _selectionSource = SyncSource.DesignSurface;
    private readonly ITypeMetadataService? _metadataService;
    private readonly Func<WorkspaceModel?>? _workspaceProvider;
    private readonly Func<string, System.Threading.Tasks.Task>? _openFileAsync;
    private BreakpointsViewModel? _breakpointsSource;
    private readonly ILogger<DesignerDocumentViewModel> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime LastWriteUtc, string? ClassName)> _xamlClassCache
        = new(FileSystemPathComparison.Comparer);

    /// <summary>
    /// Gets the file path of the XAML document.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the file name for display.
    /// </summary>
    public string FileName => System.IO.Path.GetFileName(FilePath);

    /// <summary>
    /// Gets the current caret line (1-based).
    /// </summary>
    public int CurrentLine => CodeEditor.CurrentLine;

    /// <summary>
    /// Gets the current caret column (1-based).
    /// </summary>
    public int CurrentColumn => CodeEditor.CurrentColumn;

    /// <summary>
    /// Gets or sets the shared breakpoints view model.
    /// </summary>
    [Reactive]
    public partial BreakpointsViewModel? Breakpoints { get; set; }

    /// <summary>
    /// Gets or sets whether the document is modified.
    /// </summary>
    [Reactive]
    public partial bool IsModified { get; set; }

    /// <summary>
    /// Gets or sets whether the file on disk carries the read-only attribute.
    /// </summary>
    [Reactive]
    public partial bool IsReadOnlyFile { get; set; }

    /// <summary>
    /// Gets the title for display (includes dirty and read-only indicators).
    /// </summary>
    public string Title => DocumentTitle.Format(FileName, IsModified, IsReadOnlyFile);

    // Sub-ViewModels
    public DesignSurfaceViewModel DesignSurface { get; }
    public CodeEditorViewModel CodeEditor { get; }

    // Services
    public SyncEngine SyncEngine { get; }
    public AstNodeMap NodeMap { get; }
    public ControlFactory ControlFactory { get; }
    public SelectionManager SelectionManager { get; }
    public ITypeMetadataService? MetadataService => _metadataService;

    /// <summary>
    /// Gets or sets the active view mode.
    /// </summary>
    [Reactive]
    public partial DocumentViewMode ViewMode { get; set; } = DocumentViewMode.Split;

    /// <summary>
    /// Gets or sets whether the external previewer replaces the in-app designer.
    /// </summary>
    [Reactive]
    public partial bool UseExternalPreviewer { get; set; }

    /// <summary>
    /// Gets or sets the split orientation when in split view.
    /// </summary>
    [Reactive]
    public partial AvOrientation SplitOrientation { get; set; } = AvOrientation.Vertical;

    /// <summary>
    /// Gets the selected AST node ID (synced between editor, tree, and designer).
    /// </summary>
    [Reactive]
    public partial Guid? SelectedNodeId { get; private set; }

    /// <summary>
    /// Gets the source of the most recent selection change.
    /// </summary>
    public SyncSource SelectionSource => _selectionSource;

    /// <summary>
    /// Updates selection with an explicit source to avoid feedback loops.
    /// </summary>
    public void SetSelectedNode(Guid? nodeId, SyncSource source)
    {
        // The source is set first, so observers of either property see the new node together with its source.
        bool sourceChanged = _selectionSource != source;
        _selectionSource = source;
        SelectedNodeId = nodeId;
        if (sourceChanged)
        {
            this.RaisePropertyChanged(nameof(SelectionSource));
        }
    }

    private void UpdateSelectedElementLines()
    {
        CodeEditor.SelectedElementLines = CodeEditor.FollowDesignerSelection
            && SelectedNodeId is { } id && NodeMap.FindById(id) is MutableAstObjectNode node
            ? CodeEditor.GetLineRangeForNode(node)
            : null;
    }

    /// <summary>
    /// Command to save the document.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }

    /// <summary>
    /// Command to close the document.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseCommand { get; }
    public event Action<DesignerDocumentViewModel>? CloseRequested;

    /// <summary>
    /// Command to switch to design view.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> DesignViewCommand { get; }

    /// <summary>
    /// Command to switch to code view.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> CodeViewCommand { get; }

    /// <summary>
    /// Command to switch to split view.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> SplitViewCommand { get; }

    /// <summary>
    /// Command to start the external previewer for this document.
    /// </summary>
    [Reactive]
    public partial System.Windows.Input.ICommand? StartPreviewerCommand { get; set; }

    /// <summary>
    /// Gets the active previewer session for this document.
    /// </summary>
    [Reactive]
    public partial PreviewerTcpSession? PreviewerSession { get; set; }

    private PreviewerState _previewerState;

    /// <summary>
    /// Gets the state of the external previewer of this document. The shell sets it where it starts, stops or loses a
    /// previewer; <see cref="PreviewerSession"/> may be null in every state, for example after a tab switch.
    /// </summary>
    public PreviewerState PreviewerState
    {
        get => _previewerState;
        internal set
        {
            if (IsDisposed || _previewerState == value) return;
            this.RaiseAndSetIfChanged(ref _previewerState, value);
            UpdatePendingThemeChoice();
        }
    }

    /// <summary>
    /// Command to navigate to the selected control definition.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> NavigateToDefinitionCommand { get; }

    public DesignerDocumentViewModel(
        string filePath,
        ITypeMetadataService? metadataService = null,
        Func<WorkspaceModel?>? workspaceProvider = null,
        Func<string, System.Threading.Tasks.Task>? openFileAsync = null,
        ILogger<DesignerDocumentViewModel>? logger = null,
        ILoggerFactory? loggerFactory = null,
        ILanguageIntellisenseRegistry? languageRegistry = null,
        Func<DesignerDocumentViewModel, System.Threading.Tasks.Task>? startPreviewerAsync = null,
        Func<DesignerDocumentViewModel, System.Threading.Tasks.Task>? updatePreviewerAsync = null)
    {
        FilePath = filePath;
        _metadataService = metadataService;
        _workspaceProvider = workspaceProvider;
        _openFileAsync = openFileAsync;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DesignerDocumentViewModel>.Instance;
        _loggerFactory = loggerFactory;

        // Create services
        XamlParsingService parsingService = new();
        XamlSerializationService serializationService = new();
        NodeMap = new AstNodeMap();
        SyncEngine = new SyncEngine(parsingService, serializationService, NodeMap);
        InitializeDesignerDiagnostics();
        // Create sub-ViewModels
        DesignSurface = new DesignSurfaceViewModel();
        SelectionManager = DesignSurface.Selection;
        CompletionProviderRegistry completionRegistry = CompletionProviderRegistry.CreateDefault();
        CodeEditor = new CodeEditorViewModel(
            filePath,
            SyncEngine,
            completionRegistry,
            languageRegistry,
            metadataService,
            _loggerFactory?.CreateLogger<CodeEditorViewModel>());
        ControlFactory = new ControlFactory(
            metadataService,
            _loggerFactory?.CreateLogger<ControlFactory>());
        InitializeDesignerThemes();
        InitializePreviewerUpdates(updatePreviewerAsync);

        if (startPreviewerAsync is not null)
        {
            ReactiveCommand<RxVoid, RxVoid> start = ReactiveCommand.CreateFromTask(
                () => startPreviewerAsync(this), this.WhenAnyValue(document => document.IsLoaded));
            StartPreviewerCommand = start;
            _disposables.Add(start);
            _disposables.Add(start.ThrownExceptions.WitnessOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(error =>
                {
                    SetPreviewerLaunchError(error.Message);
                    PreviewerState = PreviewerState.Failed;
                }));
            UseExternalPreviewer = _workspaceProvider?.Invoke() is { Projects.Count: > 0 };
        }

        // Track modification state
        InitializeDesignerFileState();
        IDisposable isModifiedSubscription = this.WhenAnyValue(x => x.CodeEditor.IsModified)
            .Subscribe(_ => UpdateFileModifiedState());
        _disposables.Add(isModifiedSubscription);

        // CurrentLine and CurrentColumn forward the code editor caret; the status bar observes this document.
        IDisposable caretPositionSubscription = this.WhenAnyValue(x => x.CodeEditor.CurrentLine, x => x.CodeEditor.CurrentColumn)
            .Skip(1)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(CurrentLine));
                this.RaisePropertyChanged(nameof(CurrentColumn));
            });
        _disposables.Add(caretPositionSubscription);

        // Watch for property changes to raise Title notification
        IDisposable titleSubscription = this.WhenAnyValue(x => x.IsModified, x => x.IsReadOnlyFile)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(Title)));
        _disposables.Add(titleSubscription);

        // Sync caret→node from code editor to selected node
        CodeEditor.CaretNodeChanged += nodeId =>
        {
            if (nodeId is null)
            {
                return;
            }

            SetSelectedNode(nodeId, SyncSource.CodeEditor);
        };

        // When selected node changes, update property editor
        // Sync selection to design surface and code editor caret
        IDisposable selectedNodeSyncSubscription = this.WhenAnyValue(x => x.SelectedNodeId)
            .Subscribe(id =>
            {
                SyncSource source = SelectionSource;
                UpdateSelectedElementLines();
                if (id is null)
                {
                    if (source != SyncSource.DesignSurface)
                    {
                        DesignSurface.ClearSelectionFromSync();
                    }
                    return;
                }

                MutableAstNode? node = NodeMap.FindById(id.Value);
                if (node is MutableAstObjectNode objNode)
                {
                    int? offset = CodeEditor.GetOffsetForNode(objNode);
                    if (offset is not null)
                    {
                        if (source != SyncSource.CodeEditor && CodeEditor.FollowDesignerSelection)
                        {
                            CodeEditor.SetCaretOffsetFromSync(offset.Value);
                            // Keeps the element start clear of the viewport edge instead of scrolling it just into view.
                            CodeEditor.RevealLine(objNode.Line);
                        }
                    }

                    if (source != SyncSource.DesignSurface)
                    {
                        DesignSurface.SelectByAstNodeIdFromSync(id.Value);
                    }
                }
            });
        _disposables.Add(selectedNodeSyncSubscription);

        // A text change moves the lines of the selected element; the gutter marker follows the reparsed tree.
        IDisposable selectedElementLinesSubscription = SyncEngine.SyncEvents
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => UpdateSelectedElementLines());
        _disposables.Add(selectedElementLinesSubscription);
        IDisposable followDesignerSelectionSubscription = this.WhenAnyValue(x => x.CodeEditor.FollowDesignerSelection)
            .Skip(1)
            .Subscribe(_ => UpdateSelectedElementLines());
        _disposables.Add(followDesignerSelectionSubscription);

        void OnDesignSurfaceSelectionChanged(IReadOnlyList<IDesignItem> _)
        {
            if (DesignSurface.IsSelectionSyncing)
            {
                return;
            }

            Guid? selectedId = DesignSurface.Selection.PrimarySelection?.AstNodeId;
            SetSelectedNode(selectedId, SyncSource.DesignSurface);
        }

        DesignSurface.Selection.SelectionChanged += OnDesignSurfaceSelectionChanged;
        _disposables.Add(Disposable.Create(() =>
            DesignSurface.Selection.SelectionChanged -= OnDesignSurfaceSelectionChanged));

        // Listen for sync events to update trees
        IDisposable syncEventsSubscription = SyncEngine.SyncEvents
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                // After any sync, commit pending changes as an undo batch
                SyncEngine.CommitUndoBatch("Sync");

                // Layout changes can keep the existing control instances.
                DesignSurface.RequestRebuild(allowLayoutUpdate: true);
            });
        _disposables.Add(syncEventsSubscription);

        SaveCommand = ReactiveCommand.CreateFromTask(SaveDocumentFileAsync,
            this.WhenAnyValue(document => document.IsLoading).Select(loading => !loading));
        // A failed save reaches the caller of Execute, which reports it; without a subscriber ReactiveUI would end the application.
        _disposables.Add(SaveCommand.ThrownExceptions.Subscribe(_ => { }));

        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this));

        DesignViewCommand = ReactiveCommand.Create(() => { ViewMode = DocumentViewMode.Design; });
        CodeViewCommand = ReactiveCommand.Create(() => { ViewMode = DocumentViewMode.Code; });
        SplitViewCommand = ReactiveCommand.Create(() => { ViewMode = DocumentViewMode.Split; });

        IObservable<bool> canNavigate = this.WhenAnyValue(x => x.SelectedNodeId)
            .Select(id => id is not null)
            .CombineLatest(
                Observable.Return(_metadataService is not null && _workspaceProvider is not null && _openFileAsync is not null),
                (hasSelection, servicesAvailable) => hasSelection && servicesAvailable);
        NavigateToDefinitionCommand = ReactiveCommand.CreateFromTask(NavigateToDefinitionAsync, canNavigate);
        _disposables.Add(NavigateToDefinitionCommand.ThrownExceptions
            .Subscribe(error => _logger.LogWarning("Navigate to definition failed: {Message}", error.Message)));

        IDisposable previewerToggleSubscription = this.WhenAnyValue(
                x => x.UseExternalPreviewer, x => x.IsLoaded, x => x.StartPreviewerCommand)
            .Where(state => state.Property1 && state.Property2 && state.Property3 is not null)
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(state =>
            {
                if (!IsDisposed && IsLoaded && UseExternalPreviewer &&
                    ReferenceEquals(state.Property3, StartPreviewerCommand) && state.Property3!.CanExecute(null))
                    state.Property3.Execute(null);
            });
        _disposables.Add(previewerToggleSubscription);

        IDisposable breakpointSourceSubscription = this.WhenAnyValue(x => x.Breakpoints)
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(HandleBreakpointsChanged);
        _disposables.Add(breakpointSourceSubscription);
    }

    private void HandleBreakpointsChanged(BreakpointsViewModel? breakpoints)
    {
        if (_breakpointsSource is not null)
        {
            _breakpointsSource.BreakpointsChanged -= OnBreakpointsChanged;
        }

        _breakpointsSource = breakpoints;
        if (_breakpointsSource is not null)
        {
            _breakpointsSource.BreakpointsChanged += OnBreakpointsChanged;
        }

        UpdateBreakpointHighlights();
    }

    private void OnBreakpointsChanged()
    {
        UpdateBreakpointHighlights();
    }

    private void UpdateBreakpointHighlights()
    {
        if (_breakpointsSource is null)
        {
            CodeEditor.BreakpointLineColorizer.UpdateLines(Array.Empty<int>());
            CodeEditor.BumpBreakpointHighlightVersion();
            return;
        }

        IEnumerable<int> lines = _breakpointsSource.Items
            .Where(entry => FileSystemPathComparison.Equals(entry.FilePath, FilePath))
            .Select(entry => entry.Line)
            .Distinct();

        CodeEditor.BreakpointLineColorizer.UpdateLines(lines);
        CodeEditor.BumpBreakpointHighlightVersion();
    }

    /// <summary>
    /// Navigates to the XAML file that defines the selected control type.
    /// </summary>
    public async System.Threading.Tasks.Task NavigateToDefinitionAsync()
    {
        if (_metadataService is null || _workspaceProvider is null || _openFileAsync is null)
        {
            return;
        }

        if (SelectedNodeId is null)
        {
            return;
        }

        MutableAstNode? node = NodeMap.FindById(SelectedNodeId.Value);
        if (node is not MutableAstObjectNode objNode)
        {
            return;
        }

        TypeMetadata? type = _metadataService.GetType(objNode.XmlNamespace, objNode.TypeName);
        if (type is null)
        {
            return;
        }

        WorkspaceModel? workspace = _workspaceProvider();
        if (workspace is null)
        {
            return;
        }

        ProjectModel? project = ProjectSelection.FindProjectForFile(workspace, FilePath);
        if (project is null)
        {
            return;
        }

        // Reading every XAML file of the project would block the UI thread.
        string[] xamlFiles = project.XamlFiles.Select(file => file.FilePath).ToArray();
        string fullTypeName = type.FullName;
        string? targetPath = await Task.Run(() => FindXamlFileForType(xamlFiles, fullTypeName));
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        await _openFileAsync(targetPath);
    }

    private string? FindXamlFileForType(IEnumerable<string> xamlFiles, string fullTypeName)
    {
        foreach (string filePath in xamlFiles)
        {
            string? className = TryGetXamlClassName(filePath);
            if (string.IsNullOrWhiteSpace(className))
            {
                continue;
            }

            if (string.Equals(className, fullTypeName, StringComparison.Ordinal))
            {
                return filePath;
            }
        }

        return null;
    }

    private string? TryGetXamlClassName(string filePath)
    {
        try
        {
            DateTime lastWrite = System.IO.File.GetLastWriteTimeUtc(filePath);
            if (_xamlClassCache.TryGetValue(filePath, out var cached) && cached.LastWriteUtc == lastWrite)
            {
                return cached.ClassName;
            }

            XDocument doc = XDocument.Load(filePath, LoadOptions.None);
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            string? className = doc.Root?.Attribute(x + "Class")?.Value;
            _xamlClassCache[filePath] = (lastWrite, className);
            return className;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads the document from disk.
    /// </summary>
    public async System.Threading.Tasks.Task LoadAsync()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        CancellationToken cancellationToken = _loadCancellation.Token;
        IsLoaded = false;
        IsLoading = true;
        LoadingStatus = UiStrings.ReadingXaml;
        try
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (!System.IO.File.Exists(FilePath)) return;
            string text = await _fileState.ReadAsync(FilePath, cancellationToken);
            LoadingStatus = UiStrings.ParsingXaml;
            await Task.Run(() => SyncEngine.LoadAsync(text, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            CodeEditor.SetTextSilently(text);
            UpdateFileModifiedState();

            LoadingStatus = UiStrings.InitializingLanguageServices;
            await CodeEditor.InitializeLanguageServicesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            IsLoaded = true;
            DesignSurface.RequestRebuild();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            IsLoading = false;
            LoadingStatus = null;
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        IsLoaded = false;
        _loadCancellation.Cancel();
        CloseRequested = null;
        if (_breakpointsSource is not null)
        {
            _breakpointsSource.BreakpointsChanged -= OnBreakpointsChanged;
        }
        _disposables.Dispose();
        CodeEditor.Dispose();
        SyncEngine.Dispose();
        _loadCancellation.Dispose();
    }
}

/// <summary>
/// ViewModel for a non-XAML text document.
/// </summary>
public sealed partial class TextDocumentViewModel : ReactiveObject, IEditorDocumentViewModel, ITextEditorDisplayState
{
    private readonly CompositeDisposable _disposables = new();
    public bool IsDisposed { get; private set; }
    private readonly ILanguageIntellisenseService? _languageService;
    private readonly ILanguageDocumentSync? _documentSyncService;
    private readonly ILanguageDiagnosticsSource? _diagnosticsSource;
    private bool _suppressTextChanged;
    private BreakpointsViewModel? _breakpointsSource;

    public string FilePath { get; }

    public string FileName => System.IO.Path.GetFileName(FilePath);

    public AvaloniaEdit.Document.TextDocument Document { get; } = new();

    [Reactive]
    public partial int CaretOffset { get; set; }

    [Reactive]
    public partial int SelectionStart { get; set; }

    [Reactive]
    public partial int SelectionLength { get; set; }

    [Reactive]
    public partial int CurrentLine { get; set; } = 1;

    [Reactive]
    public partial int CurrentColumn { get; set; } = 1;

    [Reactive]
    public partial bool IsModified { get; set; }

    /// <summary>Gets or sets whether the file on disk carries the read-only attribute.</summary>
    [Reactive]
    public partial bool IsReadOnlyFile { get; set; }

    [Reactive]
    public partial bool WordWrap { get; set; }

    [Reactive]
    public partial bool ShowLineNumbers { get; set; } = true;

    [Reactive]
    public partial double FontSize { get; set; } = 14.0;

    public string? LanguageId { get; }

    public BatchObservableCollection<LanguageDiagnostic> Diagnostics { get; } = new();

    public XamlVisualEditor.CodeEditor.LanguageDiagnosticColorizer DiagnosticColorizer { get; } = new();

    public XamlVisualEditor.CodeEditor.SemanticTokenColorizer SemanticTokenColorizer { get; } = new();

    public XamlVisualEditor.CodeEditor.ExecutionLineColorizer ExecutionLineColorizer { get; } = new();

    public XamlVisualEditor.CodeEditor.BreakpointLineColorizer BreakpointLineColorizer { get; } = new();

    [Reactive]
    public partial int? ExecutionLine { get; set; }

    [Reactive]
    public partial int BreakpointHighlightVersion { get; private set; }

    [Reactive]
    public partial int SemanticTokenVersion { get; private set; }

    /// <summary>
    /// Gets or sets the shared breakpoints view model.
    /// </summary>
    [Reactive]
    public partial BreakpointsViewModel? Breakpoints { get; set; }

    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }

    public TextDocumentViewModel(string filePath, ILanguageIntellisenseRegistry? languageRegistry = null)
    {
        FilePath = filePath;
        TrackDocumentHistory();
        InitializeReadOnlyState();
        LanguageId = GetLanguageIdForFile(filePath);
        _languageService = languageRegistry?.GetService(filePath, LanguageId);
        _documentSyncService = _languageService as ILanguageDocumentSync;
        _diagnosticsSource = _languageService as ILanguageDiagnosticsSource;

        IDisposable textChangedSubscription = Observable.FromEventPattern<EventHandler, EventArgs>(
            h => Document.TextChanged += h,
            h => Document.TextChanged -= h)
            .Where(_ => !_suppressTextChanged)
            .Subscribe(_ => UpdateFileModifiedState());
        _disposables.Add(textChangedSubscription);

        IDisposable diagnosticsSubscription = Observable.FromEventPattern<EventHandler, EventArgs>(
            h => Document.TextChanged += h,
            h => Document.TextChanged -= h)
            .Where(_ => !_suppressTextChanged && _languageService is not null)
            .Throttle(TimeSpan.FromMilliseconds(400))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(evt => { _ = RefreshDiagnosticsAsync(); });
        _disposables.Add(diagnosticsSubscription);

        IDisposable semanticTokensSubscription = Observable.FromEventPattern<EventHandler, EventArgs>(
            h => Document.TextChanged += h,
            h => Document.TextChanged -= h)
            .Where(_ => !_suppressTextChanged && _languageService is not null)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(evt => { _ = RefreshSemanticTokensAsync(); });
        _disposables.Add(semanticTokensSubscription);

        IDisposable syncSubscription = Observable.FromEventPattern<EventHandler, EventArgs>(
            h => Document.TextChanged += h,
            h => Document.TextChanged -= h)
            .Where(_ => !_suppressTextChanged && _documentSyncService is not null)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .Subscribe(evt => { _ = NotifyDocumentChangedAsync(); });
        _disposables.Add(syncSubscription);

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        // A failed save reaches the caller of Execute, which reports it; without a subscriber ReactiveUI would end the application.
        _disposables.Add(SaveCommand.ThrownExceptions.Subscribe(_ => { }));

        if (_diagnosticsSource is not null)
        {
            _diagnosticsSource.DiagnosticsChanged += OnDiagnosticsChanged;
            _disposables.Add(Disposable.Create(() => _diagnosticsSource.DiagnosticsChanged -= OnDiagnosticsChanged));
        }

        IDisposable breakpointSourceSubscription = this.WhenAnyValue(x => x.Breakpoints)
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(HandleBreakpointsChanged);
        _disposables.Add(breakpointSourceSubscription);
    }

    private void HandleBreakpointsChanged(BreakpointsViewModel? breakpoints)
    {
        if (_breakpointsSource is not null)
        {
            _breakpointsSource.BreakpointsChanged -= OnBreakpointsChanged;
        }

        _breakpointsSource = breakpoints;
        if (_breakpointsSource is not null)
        {
            _breakpointsSource.BreakpointsChanged += OnBreakpointsChanged;
        }

        UpdateBreakpointHighlights();
    }

    private void OnBreakpointsChanged()
    {
        UpdateBreakpointHighlights();
    }

    private void OnDiagnosticsChanged(object? sender, LanguageDiagnosticsChangedEventArgs e)
    {
        if (!FileSystemPathComparison.Equals(e.FilePath, FilePath))
        {
            return;
        }

        _ = RefreshDiagnosticsAsync();
    }

    private void UpdateBreakpointHighlights()
    {
        if (_breakpointsSource is null)
        {
            BreakpointLineColorizer.UpdateLines(Array.Empty<int>());
            BreakpointHighlightVersion++;
            return;
        }

        IEnumerable<int> lines = _breakpointsSource.Items
            .Where(entry => FileSystemPathComparison.Equals(entry.FilePath, FilePath))
            .Select(entry => entry.Line)
            .Distinct();

        BreakpointLineColorizer.UpdateLines(lines);
        BreakpointHighlightVersion++;
    }

    public async System.Threading.Tasks.Task LoadAsync()
    {
        if (!System.IO.File.Exists(FilePath))
        {
            return;
        }

        string text = await ReadDocumentFileAsync();
        _suppressTextChanged = true;
        try
        {
            Document.Text = text;
            IsModified = false;
        }
        finally
        {
            _suppressTextChanged = false;
        }

        await NotifyDocumentOpenedAsync();
        await RefreshDiagnosticsAsync();
        await RefreshSemanticTokensAsync();
    }

    private async System.Threading.Tasks.Task SaveAsync()
    {
        await SaveDocumentFileAsync();
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        if (_breakpointsSource is not null)
        {
            _breakpointsSource.BreakpointsChanged -= OnBreakpointsChanged;
        }
        _disposables.Dispose();
    }

    public async Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(
        CompletionContext context,
        CancellationToken ct = default)
    {
        if (_languageService is null)
        {
            return Array.Empty<CompletionItem>();
        }

        return await _languageService.GetCompletionsAsync(context, ct).ConfigureAwait(false);
    }

    public async Task<LanguageHover?> GetHoverAsync(int offset, CancellationToken ct = default)
    {
        if (_languageService is null)
        {
            return null;
        }

        LanguagePositionContext context = new()
        {
            FilePath = FilePath,
            Text = Document.Text,
            Offset = offset
        };

        return await _languageService.GetHoverAsync(context, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LanguageLocation>> FindDefinitionsAsync(int offset, CancellationToken ct = default)
    {
        if (_languageService is null)
        {
            return Array.Empty<LanguageLocation>();
        }

        LanguagePositionContext context = new()
        {
            FilePath = FilePath,
            Text = Document.Text,
            Offset = offset
        };

        return await _languageService.FindDefinitionsAsync(context, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LanguageLocation>> FindReferencesAsync(int offset, CancellationToken ct = default)
    {
        if (_languageService is null)
        {
            return Array.Empty<LanguageLocation>();
        }

        LanguagePositionContext context = new()
        {
            FilePath = FilePath,
            Text = Document.Text,
            Offset = offset
        };

        return await _languageService.FindReferencesAsync(context, ct).ConfigureAwait(false);
    }

    public async Task<LanguageSignatureHelp?> GetSignatureHelpAsync(int offset, CancellationToken ct = default)
    {
        if (_languageService is null)
        {
            return null;
        }

        LanguagePositionContext context = new()
        {
            FilePath = FilePath,
            Text = Document.Text,
            Offset = offset
        };

        return await _languageService.GetSignatureHelpAsync(context, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TextEdit>> GetFormattingEditsAsync(CancellationToken ct = default)
    {
        if (_languageService is null)
        {
            return Array.Empty<TextEdit>();
        }

        LanguageDocumentContext context = new()
        {
            FilePath = FilePath,
            Text = Document.Text
        };

        return await _languageService.GetFormattingEditsAsync(context, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LanguageCodeAction>> GetCodeActionsAsync(
        int offset,
        int length = 0,
        CancellationToken ct = default)
    {
        if (_languageService is null)
        {
            return Array.Empty<LanguageCodeAction>();
        }

        LanguageCodeActionContext context = new()
        {
            FilePath = FilePath,
            Text = Document.Text,
            Offset = offset,
            Length = length
        };

        return await _languageService.GetCodeActionsAsync(context, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LanguageSymbol>> GetDocumentSymbolsAsync(CancellationToken ct = default)
    {
        if (_languageService is null)
        {
            return Array.Empty<LanguageSymbol>();
        }

        LanguageDocumentContext context = new()
        {
            FilePath = FilePath,
            Text = Document.Text
        };

        return await _languageService.GetDocumentSymbolsAsync(context, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LanguageSymbol>> GetWorkspaceSymbolsAsync(
        string query,
        CancellationToken ct = default)
    {
        if (_languageService is null)
        {
            return Array.Empty<LanguageSymbol>();
        }

        LanguageSymbolQuery request = new()
        {
            Query = query
        };

        return await _languageService.GetWorkspaceSymbolsAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<LanguageRenameInfo?> PrepareRenameAsync(int offset, CancellationToken ct = default)
    {
        if (_languageService is null)
        {
            return null;
        }

        LanguagePositionContext context = new()
        {
            FilePath = FilePath,
            Text = Document.Text,
            Offset = offset
        };

        return await _languageService.PrepareRenameAsync(context, ct).ConfigureAwait(false);
    }

    public async Task<LanguageWorkspaceEdit?> RenameSymbolAsync(int offset, string newName, CancellationToken ct = default)
    {
        if (_languageService is null)
        {
            return null;
        }

        LanguageRenameContext context = new()
        {
            FilePath = FilePath,
            Text = Document.Text,
            Offset = offset,
            NewName = newName
        };

        return await _languageService.RenameSymbolAsync(context, ct).ConfigureAwait(false);
    }

    public void ApplyCompletion(CompletionItem item)
    {
        if (item.TextEdit is not null)
        {
            int offset = Math.Clamp(item.TextEdit.Offset, 0, Document.TextLength);
            int length = Math.Clamp(item.TextEdit.Length, 0, Document.TextLength - offset);
            Document.Replace(offset, length, item.TextEdit.NewText);
            return;
        }

        Document.Insert(CaretOffset, item.InsertText);
    }

    public void ApplyTextEdits(IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0)
        {
            return;
        }

        TextEdit[] orderedEdits = edits.OrderByDescending(edit => edit.Offset).ToArray();
        int previousOffset = -1;
        foreach (TextEdit edit in orderedEdits)
        {
            ArgumentNullException.ThrowIfNull(edit.NewText);
            if (edit.Offset < 0 || edit.Length < 0 || edit.Offset > Document.TextLength - edit.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(edits), "A text edit is outside the document.");
            }

            if (previousOffset >= 0 && (edit.Offset == previousOffset || edit.Offset + edit.Length > previousOffset))
            {
                throw new ArgumentException("Text edits must not overlap or share a starting offset.", nameof(edits));
            }

            previousOffset = edit.Offset;
        }

        _suppressTextChanged = true;
        try
        {
            Document.BeginUpdate();
            try
            {
                foreach (TextEdit edit in orderedEdits)
                {
                    Document.Replace(edit.Offset, edit.Length, edit.NewText);
                }
            }
            finally
            {
                Document.EndUpdate();
            }
        }
        finally
        {
            _suppressTextChanged = false;
            UpdateFileModifiedState();
        }

        _ = RefreshDiagnosticsAsync();
        _ = NotifyDocumentChangedAsync();
    }

    public void SetCaretOffset(int offset)
    {
        if (Document.TextLength == 0)
        {
            CaretOffset = 0;
            return;
        }

        int clamped = Math.Clamp(offset, 0, Document.TextLength);
        CaretOffset = clamped;
    }

    /// <summary>Sets selection and caret; observers see the change only after all three values are set.</summary>
    public void SetSelection(int start, int length, int caretOffset)
    {
        int textLength = Document.TextLength;
        int clampedStart = Math.Clamp(start, 0, textLength);
        using (DelayChangeNotifications())
        {
            SelectionStart = clampedStart;
            SelectionLength = Math.Clamp(length, 0, textLength - clampedStart);
            CaretOffset = Math.Clamp(caretOffset, 0, textLength);
        }
    }

    public int GetOffsetForLineColumn(int line, int column)
    {
        int lineNumber = Math.Clamp(line, 1, Document.LineCount);
        AvaloniaEdit.Document.DocumentLine docLine = Document.GetLineByNumber(lineNumber);
        int col = Math.Max(1, column);
        int offset = Math.Min(docLine.EndOffset, docLine.Offset + col - 1);
        return offset;
    }

    private async Task RefreshDiagnosticsAsync()
    {
        if (_languageService is null)
        {
            return;
        }

        LanguageDocumentContext context = new()
        {
            FilePath = FilePath,
            Text = Document.Text
        };

        IReadOnlyList<LanguageDiagnostic> diagnostics =
            await _languageService.GetDiagnosticsAsync(context).ConfigureAwait(false);

        // One notification for the whole result: each added item would publish all diagnostics again.
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            Diagnostics.ReplaceAll(diagnostics);
            DiagnosticColorizer.UpdateDiagnostics(diagnostics);
        });
    }

    private async Task RefreshSemanticTokensAsync()
    {
        if (_languageService is null)
        {
            return;
        }

        LanguageDocumentContext context = new()
        {
            FilePath = FilePath,
            Text = Document.Text
        };

        IReadOnlyList<LanguageSemanticToken> tokens =
            await _languageService.GetSemanticTokensAsync(context).ConfigureAwait(false);

        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            SemanticTokenColorizer.UpdateTokens(tokens);
            SemanticTokenVersion++;
        });
    }

    private Task NotifyDocumentOpenedAsync()
    {
        if (_documentSyncService is null)
        {
            return Task.CompletedTask;
        }

        return _documentSyncService.DocumentOpenedAsync(new LanguageDocumentContext
        {
            FilePath = FilePath,
            Text = Document.Text
        });
    }

    private Task NotifyDocumentChangedAsync()
    {
        if (_documentSyncService is null)
        {
            return Task.CompletedTask;
        }

        return _documentSyncService.DocumentChangedAsync(new LanguageDocumentContext
        {
            FilePath = FilePath,
            Text = Document.Text
        });
    }

    private static string? GetLanguageIdForFile(string filePath)
    {
        string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".axaml" => "xml",
            ".xaml" => "xml",
            ".xml" => "xml",
            ".csproj" => "xml",
            ".props" => "xml",
            ".targets" => "xml",
            ".cs" => "csharp",
            ".fs" => "fsharp",
            ".vb" => "vb",
            ".json" => "json",
            ".yml" => "yaml",
            ".yaml" => "yaml",
            ".md" => "markdown",
            ".js" => "javascript",
            ".ts" => "typescript",
            ".css" => "css",
            ".scss" => "scss",
            ".less" => "less",
            ".html" => "html",
            ".htm" => "html",
            ".sln" => "ini",
            _ => null
        };
    }
}

/// <summary>
/// Specifies the document view mode.
/// </summary>
public enum DocumentViewMode
{
    /// <summary>Design surface only.</summary>
    Design,

    /// <summary>Code editor only.</summary>
    Code,

    /// <summary>Split view (design + code).</summary>
    Split
}

/// <summary>
/// Specifies the state of the external previewer of a document.
/// </summary>
public enum PreviewerState
{
    /// <summary>No previewer was started, or the editor stopped it.</summary>
    Stopped,

    /// <summary>The editor is starting a previewer.</summary>
    Starting,

    /// <summary>A previewer process runs and has a session.</summary>
    Running,

    /// <summary>The previewer process ended without the editor stopping it.</summary>
    Exited,

    /// <summary>The previewer could not be started.</summary>
    Failed,

    /// <summary>The start was canceled before a previewer ran; no previewer runs until a start is requested again.</summary>
    Canceled
}

/// <summary>
/// ViewModel for a toolbox item.
/// </summary>
public sealed partial class ToolboxItemViewModel : ReactiveObject
{
    /// <summary>
    /// Gets the display name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the full type name (including namespace).
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    /// Gets the XML namespace.
    /// </summary>
    public string XmlNamespace { get; }

    /// <summary>
    /// Gets the category.
    /// </summary>
    public string Category { get; }

    /// <summary>
    /// Gets or sets whether this item is visible after filtering.
    /// </summary>
    [Reactive]
    public partial bool IsVisible { get; set; } = true;

    public ToolboxItemViewModel(string displayName, string typeName, string xmlNamespace, string category)
    {
        DisplayName = displayName;
        TypeName = typeName;
        XmlNamespace = xmlNamespace;
        Category = category;
    }
}

/// <summary>
/// ViewModel for the toolbox panel.
/// </summary>
public sealed partial class ToolboxViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();

    /// <summary>
    /// Gets or sets the search/filter text.
    /// </summary>
    [Reactive]
    public partial string? SearchText { get; set; }

    /// <summary>
    /// Gets all toolbox items.
    /// </summary>
    public ObservableCollection<ToolboxItemViewModel> Items { get; } = new();

    public ToolboxViewModel()
    {
        // Register default Avalonia controls
        RegisterDefaultControls();

        // Filter items on search
        IDisposable filterSubscription = this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(filter =>
            {
                foreach (ToolboxItemViewModel item in Items)
                {
                    item.IsVisible = string.IsNullOrEmpty(filter) ||
                                     item.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase);
                }
            });
        _disposables.Add(filterSubscription);
    }

    /// <summary>
    /// Releases all subscriptions.
    /// </summary>
    public void Dispose() => _disposables.Dispose();

    private void RegisterDefaultControls()
    {
        const string ns = "https://github.com/avaloniaui";

        // Layout
        Items.Add(new("Grid", "Grid", ns, "Layout"));
        Items.Add(new("StackPanel", "StackPanel", ns, "Layout"));
        Items.Add(new("WrapPanel", "WrapPanel", ns, "Layout"));
        Items.Add(new("DockPanel", "DockPanel", ns, "Layout"));
        Items.Add(new("Canvas", "Canvas", ns, "Layout"));
        Items.Add(new("UniformGrid", "UniformGrid", ns, "Layout"));
        Items.Add(new("ScrollViewer", "ScrollViewer", ns, "Layout"));
        Items.Add(new("Border", "Border", ns, "Layout"));
        Items.Add(new("Viewbox", "Viewbox", ns, "Layout"));
        Items.Add(new("Panel", "Panel", ns, "Layout"));

        // Controls
        Items.Add(new("Button", "Button", ns, "Controls"));
        Items.Add(new("TextBlock", "TextBlock", ns, "Controls"));
        Items.Add(new("TextBox", "TextBox", ns, "Controls"));
        Items.Add(new("CheckBox", "CheckBox", ns, "Controls"));
        Items.Add(new("RadioButton", "RadioButton", ns, "Controls"));
        Items.Add(new("ComboBox", "ComboBox", ns, "Controls"));
        Items.Add(new("ListBox", "ListBox", ns, "Controls"));
        Items.Add(new("Slider", "Slider", ns, "Controls"));
        Items.Add(new("ProgressBar", "ProgressBar", ns, "Controls"));
        Items.Add(new("Image", "Image", ns, "Controls"));
        Items.Add(new("Menu", "Menu", ns, "Controls"));
        Items.Add(new("MenuItem", "MenuItem", ns, "Controls"));
        Items.Add(new("TabControl", "TabControl", ns, "Controls"));
        Items.Add(new("TabItem", "TabItem", ns, "Controls"));
        Items.Add(new("Expander", "Expander", ns, "Controls"));
        Items.Add(new("TreeView", "TreeView", ns, "Controls"));
        Items.Add(new("DataGrid", "DataGrid", ns, "Controls"));
        Items.Add(new("Calendar", "Calendar", ns, "Controls"));
        Items.Add(new("DatePicker", "DatePicker", ns, "Controls"));
        Items.Add(new("TimePicker", "TimePicker", ns, "Controls"));
        Items.Add(new("NumericUpDown", "NumericUpDown", ns, "Controls"));
        Items.Add(new("ToggleSwitch", "ToggleSwitch", ns, "Controls"));
        Items.Add(new("SplitView", "SplitView", ns, "Controls"));
    }
}

/// <summary>
/// ViewModel for the output/diagnostics panel.
/// </summary>
public sealed partial class OutputViewModel : ReactiveObject, IOutputLogSink
{
    /// <summary>
    /// Gets the output messages.
    /// </summary>
    public ObservableCollection<OutputMessage> Messages { get; } = new();

    /// <summary>
    /// Gets or sets the active filter (All, Errors, Warnings).
    /// </summary>
    [Reactive]
    public partial string ActiveFilter { get; set; } = "All";

    /// <summary>
    /// Gets the error count.
    /// </summary>
    [Reactive]
    public partial int ErrorCount { get; set; }

    /// <summary>
    /// Gets the warning count.
    /// </summary>
    [Reactive]
    public partial int WarningCount { get; set; }

    /// <summary>
    /// Command to clear the output.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearCommand { get; }

    /// <summary>
    /// Command to copy the selected message.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> CopySelectedCommand { get; }

    /// <summary>
    /// Command to copy all messages.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyAllCommand { get; }

    /// <summary>
    /// Gets or sets the selected output message.
    /// </summary>
    [Reactive]
    public partial OutputMessage? SelectedMessage { get; set; }

    /// <summary>
    /// Interaction to copy output text to the clipboard.
    /// </summary>
    public Interaction<string, RxVoid> CopyToClipboardInteraction { get; } = new();

    public OutputViewModel()
    {
        ClearCommand = ReactiveCommand.Create(() =>
        {
            Messages.Clear();
            ErrorCount = 0;
            WarningCount = 0;
            SelectedMessage = null;
        });

        IObservable<bool> hasSelection = this.WhenAnyValue(x => x.SelectedMessage)
            .Select(msg => msg is not null);
        CopySelectedCommand = ReactiveCommand.CreateFromTask(CopySelectedAsync, hasSelection);

        IObservable<bool> hasMessages = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => Messages.CollectionChanged += h,
                h => Messages.CollectionChanged -= h)
            .Select(_ => Messages.Count > 0)
            .StartWith(Messages.Count > 0);
        CopyAllCommand = ReactiveCommand.CreateFromTask(CopyAllAsync, hasMessages);
    }

    private async System.Threading.Tasks.Task CopySelectedAsync()
    {
        if (SelectedMessage is null)
        {
            return;
        }

        string text = FormatMessage(SelectedMessage);
        await CopyToClipboardInteraction.Handle(text);
    }

    private async System.Threading.Tasks.Task CopyAllAsync()
    {
        if (Messages.Count == 0)
        {
            return;
        }

        StringBuilder builder = new();
        foreach (OutputMessage message in Messages)
        {
            builder.AppendLine(FormatMessage(message));
        }

        await CopyToClipboardInteraction.Handle(builder.ToString());
    }

    public void Write(LogEntry entry)
    {
        string level = entry.Level switch
        {
            LogLevel.Trace => "Trace",
            LogLevel.Debug => "Debug",
            LogLevel.Information => "Info",
            LogLevel.Warning => "Warning",
            LogLevel.Error => "Error",
            LogLevel.Critical => "Critical",
            _ => "Info"
        };

        string message = entry.Exception is null
            ? entry.Message
            : entry.Message + Environment.NewLine + entry.Exception;

        AddOutputMessage(new OutputMessage(
            level,
            message,
            entry.Line,
            entry.Column,
            false,
            entry.FilePath));
    }

    private static string FormatMessage(OutputMessage message)
    {
        StringBuilder builder = new();
        builder.Append('[').Append(message.Level).Append("] ").Append(message.Text);

        List<string> details = new();
        if (!string.IsNullOrWhiteSpace(message.FilePath))
        {
            details.Add(message.FilePath);
        }
        if (message.Line > 0)
        {
            details.Add($"Ln {message.Line}");
        }
        if (message.Column > 0)
        {
            details.Add($"Col {message.Column}");
        }

        if (details.Count > 0)
        {
            builder.Append(" (").Append(string.Join(", ", details)).Append(')');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Adds a plain output message.
    /// </summary>
    public void AddMessage(string level, string text)
    {
        AddOutputMessage(new OutputMessage(level, text, 0, 0, false));
    }

    /// <summary>
    /// Adds a diagnostic as an output message.
    /// </summary>
    public void AddDiagnostic(XamlDiagnostic diagnostic, string? filePath = null)
    {
        void Add()
        {
            Messages.Add(new OutputMessage(
                diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => "Error",
                    DiagnosticSeverity.Warning => "Warning",
                    _ => "Info"
                },
                diagnostic.Message,
                diagnostic.Line,
                diagnostic.Column,
                true,
                filePath));

            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                ErrorCount++;
            }
            else if (diagnostic.Severity == DiagnosticSeverity.Warning)
            {
                WarningCount++;
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Add();
        }
        else
        {
            Dispatcher.UIThread.Post(Add, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Replaces existing diagnostics with the provided list.
    /// </summary>
    public void ReplaceDiagnostics(IReadOnlyList<XamlDiagnostic> diagnostics, string? filePath = null)
    {
        void Replace()
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].IsDiagnostic)
                {
                    Messages.RemoveAt(i);
                }
            }

            ErrorCount = 0;
            WarningCount = 0;

            foreach (XamlDiagnostic diagnostic in diagnostics)
            {
                AddDiagnostic(diagnostic, filePath);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Replace();
        }
        else
        {
            Dispatcher.UIThread.Post(Replace, DispatcherPriority.Background);
        }
    }

    public void ReplaceLanguageDiagnostics(IReadOnlyList<LanguageDiagnostic> diagnostics)
    {
        void Replace()
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].IsDiagnostic)
                {
                    Messages.RemoveAt(i);
                }
            }

            ErrorCount = 0;
            WarningCount = 0;

            foreach (LanguageDiagnostic diagnostic in diagnostics)
            {
                Messages.Add(new OutputMessage(
                    diagnostic.Severity.ToString(),
                    diagnostic.Message,
                    diagnostic.Range.Start.Line,
                    diagnostic.Range.Start.Column,
                    true,
                    diagnostic.FilePath));

                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    ErrorCount++;
                }
                else if (diagnostic.Severity == DiagnosticSeverity.Warning)
                {
                    WarningCount++;
                }
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Replace();
        }
        else
        {
            Dispatcher.UIThread.Post(Replace, DispatcherPriority.Background);
        }
    }

    private void AddOutputMessage(OutputMessage message)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Messages.Add(message);
            return;
        }

        Dispatcher.UIThread.Post(() => Messages.Add(message), DispatcherPriority.Background);
    }
}

/// <summary>
/// Represents a reference or definition location.
/// </summary>
public sealed partial class ReferenceLocationViewModel : ReactiveObject
{
    public string FilePath { get; }
    public int Line { get; }
    public int Column { get; }
    public string DisplayText { get; }

    public ReferenceLocationViewModel(string filePath, int line, int column, string? label = null)
    {
        FilePath = filePath;
        Line = line;
        Column = column;
        string fileName = System.IO.Path.GetFileName(filePath);
        DisplayText = label ?? $"{fileName} ({line},{column})";
    }
}

/// <summary>
/// Groups reference locations by file.
/// </summary>
public sealed partial class ReferencesGroupViewModel : ReactiveObject
{
    public string FilePath { get; }
    public string FileName { get; }
    public ObservableCollection<ReferenceLocationViewModel> Items { get; } = new();
    public string DisplayText => FileName;

    [Reactive]
    public partial bool IsExpanded { get; set; }

    public ReferencesGroupViewModel(string filePath)
    {
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
    }
}

/// <summary>
/// ViewModel for the references panel.
/// </summary>
public sealed partial class ReferencesViewModel : ReactiveObject, IDisposable
{
    private readonly Func<ReferenceLocationViewModel, System.Threading.Tasks.Task> _navigateAsync;
    private readonly CompositeDisposable _groupDisposables = new();
    private readonly CompositeDisposable _lifetimeDisposables = new();
    private readonly HashSet<string> _expandedFiles = new(FileSystemPathComparison.Comparer);
    private const string FilterPropertyPath = "Item.DisplayText";

    public ObservableCollection<ReferencesGroupViewModel> Groups { get; } = new();

    public HierarchicalModel Model { get; }

    [Reactive]
    public partial HierarchicalNode? SelectedRow { get; set; }

    [Reactive]
    public partial object? SelectedItem { get; set; }

    [Reactive]
    public partial int TotalCount { get; private set; }

    [Reactive]
    public partial string? FilterText { get; set; }

    public SortingModel SortingModel { get; }

    public FilteringModel FilteringModel { get; }

    public SearchModel SearchModel { get; }

    public ReactiveCommand<ReferenceLocationViewModel, RxVoid> OpenLocationCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> OpenSelectedCommand { get; }

    public ReferencesViewModel(Func<ReferenceLocationViewModel, System.Threading.Tasks.Task> navigateAsync)
    {
        _navigateAsync = navigateAsync;
        OpenLocationCommand = ReactiveCommand.CreateFromTask<ReferenceLocationViewModel>(_navigateAsync);
        OpenSelectedCommand = ReactiveCommand.CreateFromTask(OpenSelectedAsync);

        SortingModel = new SortingModel();
        FilteringModel = new FilteringModel();
        SearchModel = new SearchModel
        {
            HighlightMode = SearchHighlightMode.TextAndCell,
            HighlightCurrent = true,
            WrapNavigation = true,
            UpdateSelectionOnNavigate = true
        };

        var options = new HierarchicalOptions
        {
            ChildrenSelector = item => item is ReferencesGroupViewModel group ? group.Items : null,
            IsLeafSelector = item => item is ReferenceLocationViewModel,
            IsExpandedSelector = item => item is ReferencesGroupViewModel group && group.IsExpanded,
            IsExpandedSetter = (item, value) =>
            {
                if (item is ReferencesGroupViewModel group)
                {
                    group.IsExpanded = value;
                }
            },
            VirtualizeChildren = true
        };

        Model = new HierarchicalModel(options);
        Model.SetRoots(Groups);

        _lifetimeDisposables.Add(this.WhenAnyValue(x => x.SelectedRow)
            .Subscribe(row => SelectedItem = row?.Item));

        _lifetimeDisposables.Add(this.WhenAnyValue(x => x.FilterText)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplyFilterAndSearch));
    }

    public void ReplaceItems(IEnumerable<ReferenceLocationViewModel> items)
    {
        _groupDisposables.Clear();
        Groups.Clear();
        TotalCount = 0;

        foreach (IGrouping<string, ReferenceLocationViewModel> group in items.GroupBy(i => i.FilePath))
        {
            ReferencesGroupViewModel groupVm = new(group.Key);
            groupVm.IsExpanded = _expandedFiles.Contains(group.Key);
            IDisposable expandedSubscription = groupVm.WhenAnyValue(x => x.IsExpanded)
                .Subscribe(isExpanded => UpdateExpanded(group.Key, isExpanded));
            _groupDisposables.Add(expandedSubscription);
            foreach (ReferenceLocationViewModel item in group)
            {
                groupVm.Items.Add(item);
                TotalCount++;
            }
            Groups.Add(groupVm);
        }

        Model.Refresh();
        ApplyFilterAndSearch(FilterText);
    }

    private void UpdateExpanded(string filePath, bool isExpanded)
    {
        if (isExpanded)
        {
            _expandedFiles.Add(filePath);
        }
        else
        {
            _expandedFiles.Remove(filePath);
        }
    }

    private async System.Threading.Tasks.Task OpenSelectedAsync()
    {
        if (SelectedItem is ReferenceLocationViewModel location)
        {
            await _navigateAsync(location);
        }
    }

    private void ApplyFilterAndSearch(string? text)
    {
        ApplyFiltering(text);
        ApplySearch(text);
    }

    private void ApplyFiltering(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            FilteringModel.Remove(FilterPropertyPath);
            return;
        }

        string query = text.Trim();
        HashSet<object> matches = BuildMatchSet(Groups, query);
        FilteringModel.SetOrUpdate(new FilteringDescriptor(
            columnId: FilterPropertyPath,
            @operator: FilteringOperator.Custom,
            propertyPath: FilterPropertyPath,
            predicate: item => MatchesFilter(item, matches)));
    }

    private void ApplySearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SearchModel.Clear();
            return;
        }

        string query = text.Trim();
        SearchModel.SetOrUpdate(new SearchDescriptor(
            query,
            matchMode: SearchMatchMode.Contains,
            termMode: SearchTermCombineMode.Any,
            scope: SearchScope.VisibleColumns,
            comparison: StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesFilter(object? item, HashSet<object> matches)
    {
        return item is not null && matches.Contains(item);
    }

    private static HashSet<object> BuildMatchSet(
        IEnumerable<ReferencesGroupViewModel> groups,
        string text)
    {
        HashSet<object> matches = new();
        foreach (ReferencesGroupViewModel group in groups)
        {
            CollectMatches(group, text, matches);
        }

        return matches;
    }

    private static bool CollectMatches(
        ReferencesGroupViewModel group,
        string text,
        HashSet<object> matches)
    {
        bool groupMatch = group.DisplayText.Contains(text, StringComparison.OrdinalIgnoreCase)
            || group.FilePath.Contains(text, StringComparison.OrdinalIgnoreCase);
        bool childMatch = false;

        foreach (ReferenceLocationViewModel item in group.Items)
        {
            if (item.DisplayText.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(item);
                childMatch = true;
            }
        }

        if (groupMatch)
        {
            matches.Add(group);
            foreach (ReferenceLocationViewModel item in group.Items)
            {
                matches.Add(item);
            }

            return true;
        }

        if (childMatch)
        {
            matches.Add(group);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Ends the filter and expansion subscriptions. The throttled filter would otherwise still reach the UI
    /// dispatcher after the shell is gone.
    /// </summary>
    public void Dispose()
    {
        _lifetimeDisposables.Dispose();
        _groupDisposables.Dispose();
    }
}

public sealed partial record DefinitionPickerRequest(
    string Title,
    IReadOnlyList<ReferenceLocationViewModel> Items);

public sealed partial record CodeActionPickerRequest(
    string Title,
    IReadOnlyList<LanguageCodeAction> Items);

/// <summary>
/// ViewModel for the definition picker dialog.
/// </summary>
public sealed partial class DefinitionPickerDialogViewModel : ReactiveObject
{
    public string Title { get; }

    public ObservableCollection<ReferenceLocationViewModel> Items { get; } = new();

    [Reactive]
    public partial ReferenceLocationViewModel? SelectedItem { get; set; }

    public Interaction<ReferenceLocationViewModel?, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> OpenCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public DefinitionPickerDialogViewModel(DefinitionPickerRequest request)
    {
        Title = request.Title;
        foreach (ReferenceLocationViewModel item in request.Items)
        {
            Items.Add(item);
        }

        if (Items.Count > 0)
        {
            SelectedItem = Items[0];
        }

        IObservable<bool> canOpen = this.WhenAnyValue(x => x.SelectedItem)
            .Select(item => item is not null);

        OpenCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(SelectedItem), canOpen);
        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(null));
    }
}

public sealed partial class CodeActionItemViewModel : ReactiveObject
{
    public CodeActionItemViewModel(LanguageCodeAction action)
    {
        Action = action;
    }

    public LanguageCodeAction Action { get; }

    public string Title => Action.Title;

    public string Kind => Action.Kind ?? string.Empty;
}

/// <summary>
/// ViewModel for the code action picker dialog.
/// </summary>
public sealed partial class CodeActionPickerDialogViewModel : ReactiveObject
{
    public string Title { get; }

    public ObservableCollection<CodeActionItemViewModel> Items { get; } = new();

    [Reactive]
    public partial CodeActionItemViewModel? SelectedItem { get; set; }

    public Interaction<LanguageCodeAction?, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> ApplyCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public CodeActionPickerDialogViewModel(CodeActionPickerRequest request)
    {
        Title = request.Title;
        foreach (LanguageCodeAction action in request.Items)
        {
            Items.Add(new CodeActionItemViewModel(action));
        }

        if (Items.Count > 0)
        {
            SelectedItem = Items[0];
        }

        IObservable<bool> canApply = this.WhenAnyValue(x => x.SelectedItem)
            .Select(item => item is not null);

        ApplyCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(SelectedItem?.Action), canApply);
        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(null));
    }
}

/// <summary>
/// ViewModel for the rename symbol dialog.
/// </summary>
public sealed partial class RenameSymbolDialogViewModel : ReactiveObject
{
    public string Title { get; }
    public string Prompt { get; }

    [Reactive]
    public partial string Name { get; set; }

    public Interaction<string?, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> ConfirmCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public RenameSymbolDialogViewModel(string title, string prompt, string name)
    {
        Title = title;
        Prompt = prompt;
        Name = name;

        ConfirmCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(Name));
        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(null));
    }
}

public enum PreviewerTrustDecision
{
    Cancel,
    AllowOnce,
    TrustWorkspace
}

public enum ThemeVariantOption
{
    Default,
    Light,
    Dark
}

public sealed partial record PreviewerTrustRequest(
    string Title,
    string Message,
    string Location);

/// <summary>
/// ViewModel for the previewer trust warning dialog.
/// </summary>
public sealed partial class PreviewerTrustDialogViewModel : ReactiveObject
{
    public string Title { get; }
    public string Message { get; }
    public string Location { get; }

    public Interaction<PreviewerTrustDecision, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> TrustCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> AllowOnceCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public PreviewerTrustDialogViewModel(PreviewerTrustRequest request)
    {
        Title = request.Title;
        Message = request.Message;
        Location = request.Location;

        TrustCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(PreviewerTrustDecision.TrustWorkspace));
        AllowOnceCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(PreviewerTrustDecision.AllowOnce));
        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(PreviewerTrustDecision.Cancel));
    }
}

/// <summary>
/// ViewModel for debug tool download consent dialog.
/// </summary>
public sealed partial class DebugToolConsentDialogViewModel : ReactiveObject
{
    public string Title { get; } = UiStrings.DownloadDebugTool;
    public string Message { get; }
    public string ToolId { get; }
    public string Version { get; }
    public string DownloadUrl { get; }
    public string InstallPath { get; }

    public Interaction<bool, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> AllowCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public DebugToolConsentDialogViewModel(DebugToolConsentRequest request)
    {
        Message = request.Message;
        ToolId = request.ToolId;
        Version = request.Version;
        DownloadUrl = request.DownloadUrl;
        InstallPath = request.InstallPath;

        AllowCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(true));
        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(false));
    }
}

/// <summary>
/// A message in the output panel.
/// </summary>
public sealed partial record OutputMessage(
    string Level,
    string Text,
    int Line,
    int Column,
    bool IsDiagnostic = false,
    string? FilePath = null);

/// <summary>
/// Represents a recent file entry.
/// </summary>
public sealed partial class RecentFileEntry
{
    public RecentFileEntry(string filePath, RecentEntryKind kind = RecentEntryKind.File)
    {
        FilePath = filePath;
        Kind = kind;
    }

    public string FilePath { get; }
    public RecentEntryKind Kind { get; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(FilePath)) is { Length: > 0 } name
        ? name : FilePath;

    [System.Text.Json.Serialization.JsonIgnore]
    public string KindDisplayName => Kind switch
    {
        RecentEntryKind.Workspace => UiStrings.MenuRecentProjects,
        RecentEntryKind.Folder => UiStrings.MenuRecentFolders,
        _ => UiStrings.MenuRecentFiles
    };
}

/// <summary>Determines how an entry of the history opens.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RecentEntryKind>))]
public enum RecentEntryKind { File, Workspace, Folder }

/// <summary>
/// Specifies how XAML changes are saved to disk.
/// </summary>
public enum SaveBehavior
{
    /// <summary>Save automatically after changes.</summary>
    AutoSave,

    /// <summary>Save only when invoked manually.</summary>
    SaveManually,

    /// <summary>Do not save changes to disk.</summary>
    NoSaving
}

internal sealed partial class WorkspaceAssemblySet
{
    public WorkspaceAssemblySet(IReadOnlyList<string> all, IReadOnlyList<string> preferred)
    {
        All = all;
        Preferred = preferred;
    }

    public IReadOnlyList<string> All { get; }

    public IReadOnlyList<string> Preferred { get; }
}

/// <summary>
/// Main window ViewModel orchestrating the docking layout and document management.
/// </summary>
public sealed partial class MainWindowViewModel : ReactiveObject, IDisposable, IWorkspaceCommands
{
    private const int RecentFilesLimit = 10;
    private readonly string? _recentFilesPath;
    private readonly EditorOptionsStore _editorOptions;
    private bool _applyingDesignerViewport;
    private readonly WorkspaceSessionStore _workspaceSessions;
    private bool _isRestoringWorkspaceSession;
    private readonly IContextKeys? _contextKeys;
    private readonly IEditorThemeService? _editorThemes;

    public ReactiveCommand<RxVoid, RxVoid> OpenOptionsCommand { get; }
    public Interaction<OptionsDialogViewModel, bool> OptionsInteraction { get; } = new();
    private readonly CompositeDisposable _disposables = new();
    private readonly SerialDisposable _statusTextExpiration = new();
    private string _statusText = UiStrings.Ready;
    private long _statusTextGeneration;
    private readonly IBackgroundActivityMonitor? _backgroundActivities;
    private readonly IDocumentOpenResolver? _documentOpenResolver;
    private string? _activityStatusText;
    private bool _suppressTreeSelectionSync;
    private string? _clipboard;
    private readonly HashSet<Guid> _visualExpandedIds = new();
    private readonly HashSet<Guid> _logicalExpandedIds = new();
    private readonly IWorkspaceService? _workspaceService;
    private readonly IWorkspaceInfoUpdater? _workspaceInfoUpdater;
    private readonly ITypeMetadataService? _metadataService;
    private readonly IControlCatalogUpdater? _controlCatalogUpdater;
    private readonly IClipboardService? _clipboardService;
    private readonly IDotNetCli _dotNetCli;
    private readonly ILanguageIntellisenseRegistry? _languageRegistry;
    private readonly PreviewerLaunchService _previewerLaunchService = new();

    /// <summary>The number of designer documents that keep a previewer host: the active one and the most recently active ones.</summary>
    internal const int MaxLivePreviewers = 4;

    // Designer documents, most recently activated first.
    private readonly List<DesignerDocumentViewModel> _previewerActivationOrder = new();

    internal PreviewerLaunchService PreviewerLaunchService => _previewerLaunchService;
    private readonly IDebuggerServiceRegistry _debuggerRegistry;
    private const string NetcoredbgServiceId = "debugger.netcoredbg";
    private readonly Dictionary<string, string?> _adapterPathsByService = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeDebuggerServiceId;
    private readonly IDebugToolInstaller? _debugToolInstaller;
    private readonly IOutputLogSinkAccessor? _outputLogSinkAccessor;
    private readonly IOutputChannel? _outputChannel;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly XamlVisualEditor.Terminal.ITerminalService? _terminalService;
    private readonly Dictionary<string, ProjectModel> _projectLookup = new(FileSystemPathComparison.Comparer);
    private System.Diagnostics.Process? _runProcess;
    private readonly HashSet<string> _trustedPreviewerRoots = new(FileSystemPathComparison.Comparer);
    private readonly Dictionary<IEditorDocumentViewModel, IDisposable> _autoSaveSubscriptions = new();
    private WorkspaceAssemblyResolver? _assemblyResolver;
    private WorkspaceModel? _workspace;
    private string? _workspacePath;
    private readonly Dictionary<string, IDockable> _dockDocuments = new(FileSystemPathComparison.Comparer);
    private readonly Dictionary<IEditorDocumentViewModel, IDisposable> _dockTitleSubscriptions = new();
    private readonly Dictionary<Guid, IDisposable> _terminalTitleSubscriptions = new();
    private bool _isClosingFromDock;
    private readonly ObservableAsPropertyHelper<int> _activeLine;
    private readonly ObservableAsPropertyHelper<int> _activeColumn;
    private readonly ObservableAsPropertyHelper<object?> _activeTextEditorDocument;
    private bool _isLoadingRecentFiles;
    private InfiniteCanvasDocument? _canvasDocument;
    private readonly Dictionary<Guid, TerminalTool> _terminalTools = new();
    private readonly Stack<LanguageLocation> _backNavigation = new();
    private readonly Stack<LanguageLocation> _forwardNavigation = new();
    private readonly IExtensionContributionRegistry? _extensionContributions;
    private readonly IExtensionViewRegistry? _extensionViewRegistry;
    private readonly ICommands? _extensionCommands;
    private readonly ICommandMetadataRegistry? _commandMetadata;
    private readonly Dictionary<string, ExtensionTool> _extensionTools = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _placedExtensionViews = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string>? _presetPanels;
    private readonly IUserLayoutStore _userLayoutStore;
    private readonly IReadOnlyList<LayoutPreset> _layoutPresets = LayoutPresetCatalog.Create();
    private readonly Dictionary<string, ExtensionViewModel> _extensionViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ICommand> _extensionCommandsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _extensionCommandSubscriptionsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionStatusBarItemViewModel> _statusBarItemsById = new(StringComparer.Ordinal);
    private string? _activeExtensionViewId;
    private int ClipboardVersion { get; set; }

    /// <summary>
    /// Gets the open documents.
    /// </summary>
    public ObservableCollection<IEditorDocumentViewModel> Documents { get; } = new();

    /// <summary>
    /// Gets or sets the active document.
    /// </summary>
    [Reactive]
    public partial IEditorDocumentViewModel? ActiveDocument { get; set; }

    [Reactive]
    public partial bool CanNavigateBack { get; private set; }

    [Reactive]
    public partial bool CanNavigateForward { get; private set; }

    public int ActiveLine => _activeLine.Value;

    public int ActiveColumn => _activeColumn.Value;

    /// <summary>The document whose text editor is on screen: a text document, or the code editor of a designer
    /// document outside the design-only view; null when no text editor shows.</summary>
    public object? ActiveTextEditorDocument => _activeTextEditorDocument.Value;

    /// <summary>Tells whether a text editor shows, which the line and column of the status bar refer to.</summary>
    public bool HasVisibleTextEditor => ActiveTextEditorDocument is not null;

    [Reactive]
    public partial DesignerDocumentViewModel? ActiveDesignerDocument { get; private set; }

    [Reactive]
    public partial TextDocumentViewModel? ActiveTextDocument { get; private set; }

    /// <summary>
    /// Gets the toolbox ViewModel.
    /// </summary>
    public ToolboxViewModel Toolbox { get; } = new();

    /// <summary>
    /// Gets the solution explorer ViewModel.
    /// </summary>
    public SolutionExplorerViewModel SolutionExplorer { get; }

    /// <summary>Gets the commands of the context menu of a file document tab.</summary>
    public DocumentTabCommands DocumentTabCommands { get; }

    /// <summary>
    /// Gets the workspace projects.
    /// </summary>
    public ObservableCollection<ProjectModel> WorkspaceProjects { get; } = new();

    /// <summary>
    /// Gets the open terminal sessions.
    /// </summary>
    public ObservableCollection<TerminalViewModel> Terminals { get; } = new();

    /// <summary>
    /// Gets the output ViewModel.
    /// </summary>
    public OutputViewModel Output { get; } = new();

    /// <summary>
    /// Gets extension-provided menu items.
    /// </summary>
    public ObservableCollection<ExtensionMenuItemViewModel> ExtensionMenuItems { get; } = new();

    /// <summary>
    /// Gets extension-provided File menu items.
    /// </summary>
    public ObservableCollection<ExtensionMenuItemViewModel> FileMenuItems { get; } = new();

    /// <summary>
    /// Gets extension-provided File > New menu items.
    /// </summary>
    public ObservableCollection<ExtensionMenuItemViewModel> FileNewMenuItems { get; } = new();

    /// <summary>
    /// Gets File > New menu entries (built-in + extensions).
    /// </summary>
    public ObservableCollection<ExtensionMenuItemViewModel> FileNewMenuEntries { get; } = new();

    /// <summary>
    /// Gets extension-provided View menu items.
    /// </summary>
    public ObservableCollection<ExtensionMenuItemViewModel> ViewMenuItems { get; } = new();

    /// <summary>Gets the extension-provided items of the text and code editor context menu.</summary>
    public ObservableCollection<ExtensionMenuItemViewModel> EditorContextMenuItems { get; } = new();

    /// <summary>Holds the entries under View → Server.</summary>
    public ObservableCollection<ExtensionMenuItemViewModel> ServerMenuItems { get; } = new();

    /// <summary>Gets the ready-made layouts offered under View → Layout → Load.</summary>
    public ObservableCollection<ExtensionMenuItemViewModel> LayoutPresetMenuItems { get; } = new();

    /// <summary>Gets the user-saved layouts offered under View → Layout → Load.</summary>
    public ObservableCollection<ExtensionMenuItemViewModel> SavedLayoutMenuItems { get; } = new();

    /// <summary>Gets whether at least one user-saved layout exists.</summary>
    public bool HasSavedLayouts => SavedLayoutMenuItems.Count > 0;

    /// <summary>Gets the name of the user layout that was loaded or saved last; null for presets and the default layout.</summary>
    [Reactive]
    public partial string? CurrentLayoutName { get; private set; }

    /// <summary>
    /// Gets the status bar text for the active layout: null while no layout was loaded explicitly,
    /// "unnamed" for presets and unsaved layouts, otherwise the saved layout name.
    /// </summary>
    [Reactive]
    public partial string? LayoutStatusText { get; private set; }

    /// <summary>Gets the header of the "delete layout" menu entry; names the active user layout when there is one.</summary>
    public string DeleteLayoutMenuHeader => CurrentLayoutName is null
        ? UiStrings.MenuLayoutDelete
        : string.Format(UiStrings.MenuLayoutDeleteNamed, CurrentLayoutName);

    /// <summary>
    /// Gets extension-provided Debug menu items.
    /// </summary>
    public ObservableCollection<ExtensionMenuItemViewModel> DebugMenuItems { get; } = new();

    /// <summary>
    /// Gets extension-provided Edit menu items.
    /// </summary>
    public ObservableCollection<ExtensionMenuItemViewModel> EditMenuItems { get; } = new();

    /// <summary>
    /// Gets extension-provided Tools > Workspace menu items.
    /// </summary>
    public ObservableCollection<ExtensionMenuItemViewModel> WorkspaceMenuItems { get; } = new();

    /// <summary>
    /// Gets extension-provided toolbar items.
    /// </summary>
    public ObservableCollection<ExtensionToolbarItemViewModel> ExtensionToolbarItems { get; } = new();

    /// <summary>
    /// Gets extension-provided main toolbar items.
    /// </summary>
    public ObservableCollection<ExtensionToolbarItemViewModel> MainToolbarItems { get; } = new();

    /// <summary>
    /// Gets extension-provided command palette items.
    /// </summary>
    public ObservableCollection<ExtensionCommandPaletteItemViewModel> CommandPaletteItems { get; } = new();

    /// <summary>
    /// Gets extension-provided left status bar items.
    /// </summary>
    public ObservableCollection<ExtensionStatusBarItemViewModel> LeftStatusBarItems { get; } = new();

    /// <summary>
    /// Gets extension-provided right status bar items.
    /// </summary>
    public ObservableCollection<ExtensionStatusBarItemViewModel> RightStatusBarItems { get; } = new();

    /// <summary>
    /// Gets extension-provided keybinding items.
    /// </summary>
    public ObservableCollection<ExtensionKeyBindingViewModel> ExtensionKeyBindings { get; } = new();

    /// <summary>
    /// Gets extension-provided view models.
    /// </summary>
    public ObservableCollection<ExtensionViewModel> ExtensionViews { get; } = new();

    /// <summary>
    /// Gets the extension manager ViewModel.
    /// </summary>
    public ExtensionManagerViewModel ExtensionManager { get; }

    /// <summary>
    /// Raised when extension view visibility changes.
    /// </summary>
    public event EventHandler<ExtensionViewVisibilityChangedEventArgs>? ExtensionViewVisibilityChanged;

    /// <summary>
    /// Raised when extension view focus changes.
    /// </summary>
    public event EventHandler<ExtensionViewFocusChangedEventArgs>? ExtensionViewFocusChanged;

    public bool HasExtensionToolbarItems => ExtensionToolbarItems.Count > 0;

    /// <summary>
    /// Gets the debugger ViewModel.
    /// </summary>
    public DebuggerViewModel Debugger { get; }

    /// <summary>
    /// Gets the debug settings ViewModel.
    /// </summary>
    public DebugSettingsViewModel DebugSettings { get; }

    /// <summary>
    /// Gets the LSP settings ViewModel.
    /// </summary>
    public LspSettingsViewModel LspSettings { get; }

    /// <summary>
    /// Gets the breakpoints ViewModel.
    /// </summary>
    public BreakpointsViewModel Breakpoints => Debugger.Breakpoints;

    /// <summary>
    /// Gets the call stack ViewModel.
    /// </summary>
    public CallStackViewModel CallStack => Debugger.CallStack;

    /// <summary>
    /// Gets the locals ViewModel.
    /// </summary>
    public LocalsViewModel Locals => Debugger.Locals;

    /// <summary>
    /// Gets the watches ViewModel.
    /// </summary>
    public WatchesViewModel Watches => Debugger.Watches;

    /// <summary>
    /// Gets the active startup project.
    /// </summary>
    [Reactive]
    public partial ProjectModel? ActiveProject { get; private set; }

    [Reactive]
    public partial string? ActiveProjectPath { get; private set; }

    public string ActiveProjectName => ActiveProject?.Name ?? "(none)";

    /// <summary>
    /// Gets the references ViewModel.
    /// </summary>
    public ReferencesViewModel References { get; }

    /// <summary>
    /// Gets the infinite canvas ViewModel.
    /// </summary>
    public InfiniteCanvasViewModel InfiniteCanvas { get; }

    /// <summary>
    /// Gets or sets the save behavior for XAML changes.
    /// </summary>
    [Reactive]
    public partial SaveBehavior SaveBehavior { get; set; } = SaveBehavior.SaveManually;

    public bool IsAutoSaveSelected => SaveBehavior == SaveBehavior.AutoSave;
    public bool IsManualSaveSelected => SaveBehavior == SaveBehavior.SaveManually;
    public bool IsNoSaveSelected => SaveBehavior == SaveBehavior.NoSaving;

    /// <summary>
    /// Gets or sets the selected theme variant for previews.
    /// </summary>
    [Reactive]
    public partial ThemeVariantOption SelectedThemeVariant { get; set; } = ThemeVariantOption.Dark;

    public bool IsThemeDefaultSelected => SelectedThemeVariant == ThemeVariantOption.Default;
    public bool IsThemeLightSelected => SelectedThemeVariant == ThemeVariantOption.Light;
    public bool IsThemeDarkSelected => SelectedThemeVariant == ThemeVariantOption.Dark;

    /// <summary>
    /// Gets the visual tree grid ViewModel for the active document.
    /// </summary>
    public VisualTreeGridViewModel VisualTree { get; } = new();

    /// <summary>
    /// Gets the logical tree grid ViewModel for the active document.
    /// </summary>
    public LogicalTreeGridViewModel LogicalTree { get; } = new();

    /// <summary>
    /// Gets the collaboration panel ViewModel.
    /// </summary>
    public CollaborationPanelViewModel Collaboration { get; } = new();

    /// <summary>
    /// Gets the animation editor ViewModel.
    /// </summary>
    public AnimationEditorViewModel AnimationEditor { get; }

    /// <summary>
    /// Gets the dock factory.
    /// </summary>
    public XamlEditorDockFactory DockFactory { get; }

    /// <summary>
    /// Gets the active dock layout.
    /// </summary>
    [Reactive]
    public partial IRootDock DockLayout { get; private set; } = new RootDock
    {
        Id = "Root",
        Title = UiStrings.RootArea,
        VisibleDockables = new ObservableCollection<IDockable>(),
        LeftPinnedDockables = new ObservableCollection<IDockable>(),
        RightPinnedDockables = new ObservableCollection<IDockable>(),
        TopPinnedDockables = new ObservableCollection<IDockable>(),
        BottomPinnedDockables = new ObservableCollection<IDockable>()
    };

    /// <summary>
    /// Gets or sets the application title.
    /// </summary>
    [Reactive]
    public partial string Title { get; set; } = UiStrings.ApplicationName;

    /// <summary>
    /// Gets or sets the status bar text.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        set => SetStatusText(value, persistent: false);
    }

    /// <summary>
    /// Gets whether the background activities that ran when the view model was created have not all ended yet.
    /// </summary>
    [Reactive]
    public partial bool IsStarting { get; private set; }

    private void SetPersistentStatus(string message) => SetStatusText(message, persistent: true);

    // Without a subscriber to ThrownExceptions ReactiveUI ends the application when a command fails.
    private void ReportCommandFailures(string messageFormat, params IHandleObservableErrors[] commands)
    {
        foreach (IHandleObservableErrors command in commands)
        {
            _disposables.Add(command.ThrownExceptions
                .WitnessOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(error =>
                {
                    _logger.LogWarning(error, "Command failed: {Message}", error.Message);
                    SetPersistentStatus(string.Format(messageFormat, (object?)error.Message));
                }));
        }
    }

    private void SetStatusText(string message, bool persistent)
    {
        long generation = Interlocked.Increment(ref _statusTextGeneration);
        this.RaiseAndSetIfChanged(ref _statusText, message, nameof(StatusText));
        _statusTextExpiration.Disposable = null;
        if (persistent || string.IsNullOrWhiteSpace(message) || string.Equals(message, UiStrings.Ready, StringComparison.Ordinal))
            return;

        _statusTextExpiration.Disposable = Observable.Timer(TimeSpan.FromSeconds(30))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (Interlocked.Read(ref _statusTextGeneration) == generation)
                    ShowIdleStatus();
            });
    }

    // "Ready" is the idle text only after startup; until then a running activity or the startup text stands in.
    private void ShowIdleStatus()
    {
        string? activity = _backgroundActivities?.CurrentDescription;
        _activityStatusText = activity;
        SetPersistentStatus(activity ?? (IsStarting ? UiStrings.StartingStatus : UiStrings.Ready));
    }

    // An activity replaces only an activity text or the idle text; any other message, such as the progress of a
    // workspace load, stays until it expires and the idle text follows.
    private void OnBackgroundActivitiesChanged()
    {
        string? activity = _backgroundActivities!.CurrentDescription;
        bool showsActivityOrIdle = string.IsNullOrWhiteSpace(_statusText)
            || string.Equals(_statusText, _activityStatusText, StringComparison.Ordinal)
            || string.Equals(_statusText, UiStrings.Ready, StringComparison.Ordinal)
            || string.Equals(_statusText, UiStrings.StartingStatus, StringComparison.Ordinal);
        if (activity is not null)
        {
            if (showsActivityOrIdle)
            {
                _activityStatusText = activity;
                SetPersistentStatus(activity);
            }

            return;
        }

        _activityStatusText = null;
        IsStarting = false;
        if (showsActivityOrIdle)
            SetPersistentStatus(UiStrings.Ready);
    }

    /// <summary>
    /// Gets the recent files list.
    /// </summary>
    public ObservableCollection<RecentFileEntry> RecentFiles { get; } = new();

    /// <summary>The ten most recent entries of each open kind.</summary>
    public IEnumerable<RecentFileEntry> RecentWorkspaces => RecentFiles.Where(entry => entry.Kind == RecentEntryKind.Workspace).Take(RecentFilesLimit);
    public IEnumerable<RecentFileEntry> RecentDocuments => RecentFiles.Where(entry => entry.Kind == RecentEntryKind.File).Take(RecentFilesLimit);
    public IEnumerable<RecentFileEntry> RecentFolders => RecentFiles.Where(entry => entry.Kind == RecentEntryKind.Folder).Take(RecentFilesLimit);
    public bool HasRecentWorkspaces => RecentWorkspaces.Any();
    public bool HasRecentDocuments => RecentDocuments.Any();
    public bool HasRecentFolders => RecentFolders.Any();

    public bool HasRecentFiles => RecentFiles.Count > 0;
    public ReactiveCommand<RecentFileEntry, RxVoid> OpenRecentCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClearRecentFilesCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ManageRecentFilesCommand { get; }
    public Interaction<RecentFilesDialogViewModel, RxVoid> RecentFilesInteraction { get; } = new();

    /// <summary>
    /// Gets whether a workspace is currently loaded.
    /// </summary>
    [Reactive]
    public partial bool HasWorkspace { get; private set; }

    /// <summary>
    /// Gets whether a workspace load is in progress; drives the busy overlay.
    /// </summary>
    [Reactive]
    public partial bool IsWorkspaceLoading { get; private set; }

    /// <summary>
    /// Gets whether a workspace restore, build, rebuild, or clean command is running.
    /// </summary>
    [Reactive]
    public partial bool IsWorkspaceCommandRunning { get; private set; }

    /// <summary>
    /// Interaction for opening a file dialog.
    /// </summary>
    public Interaction<RxVoid, string?> OpenFileInteraction { get; } = new();

    /// <summary>
    /// Selects a project or solution file as the workspace.
    /// </summary>
    public Interaction<RxVoid, WorkspaceOpenRequest?> OpenWorkspaceInteraction { get; } = new();

    /// <summary>
    /// Interaction for selecting an extension package.
    /// </summary>
    public Interaction<RxVoid, string?> ExtensionPackageOpenInteraction { get; } = new();

    public Interaction<RxVoid, RxVoid> ExtensionManagerInteraction { get; } = new();

    /// <summary>
    /// Interaction for saving a file dialog.
    /// </summary>
    public Interaction<string, string?> SaveFileInteraction { get; } = new();

    /// <summary>
    /// Interaction for prompting rename input.
    /// </summary>
    public Interaction<LanguageRenameInfo, string?> RenameSymbolInteraction { get; } = new();

    /// <summary>
    /// Interaction for selecting between multiple definitions.
    /// </summary>
    public Interaction<DefinitionPickerRequest, ReferenceLocationViewModel?> SelectDefinitionInteraction { get; } = new();

    /// <summary>
    /// Interaction for selecting a code action to apply.
    /// </summary>
    public Interaction<CodeActionPickerRequest, LanguageCodeAction?> SelectCodeActionInteraction { get; } = new();

    /// <summary>
    /// Interaction for prompting workspace symbol queries.
    /// </summary>
    public Interaction<string, string?> WorkspaceSymbolQueryInteraction { get; } = new();

    /// <summary>
    /// Interaction for prompting the name of a layout to save. Input is the suggested name, output the chosen name or null.
    /// </summary>
    public Interaction<string, string?> LayoutNameInteraction { get; } = new();

    /// <summary>Asks the user to confirm deleting the named layout; the output is true when confirmed.</summary>
    public Interaction<string, bool> ConfirmLayoutDeleteInteraction { get; } = new();

    /// <summary>Shows the modal dialog that renames and deletes saved layouts.</summary>
    public Interaction<ManageLayoutsDialogViewModel, RxVoid> ManageLayoutsInteraction { get; } = new();

    /// <summary>
    /// Interaction for command palette selection.
    /// </summary>
    public Interaction<CommandPaletteRequest, ExtensionCommandPaletteItemViewModel?> CommandPaletteInteraction { get; } = new();

    /// <summary>
    /// Interaction for previewer trust warnings.
    /// </summary>
    public Interaction<PreviewerTrustRequest, PreviewerTrustDecision> PreviewerTrustInteraction { get; } = new();

    /// <summary>
    /// Interaction for applying theme variants.
    /// </summary>

    /// <summary>
    /// Interaction for debug tool download consent.
    /// </summary>
    public Interaction<DebugToolConsentRequest, bool> DebugToolConsentInteraction { get; } = new();


    // Panel visibility
    [Reactive] public partial bool IsToolboxVisible { get; set; } = true;
    [Reactive] public partial bool IsPropertiesVisible { get; set; } = true;
    [Reactive] public partial bool IsVisualTreeVisible { get; set; } = true;
    [Reactive] public partial bool IsLogicalTreeVisible { get; set; } = true;
    [Reactive] public partial bool IsOutputVisible { get; set; } = true;
    [Reactive] public partial bool IsReferencesVisible { get; set; }
    [Reactive] public partial bool IsCollaborationVisible { get; set; }
    [Reactive] public partial bool IsAnimationEditorVisible { get; set; } = true;
    [Reactive] public partial bool IsBreakpointsVisible { get; set; } = true;
    [Reactive] public partial bool IsCallStackVisible { get; set; } = true;
    [Reactive] public partial bool IsLocalsVisible { get; set; } = true;
    [Reactive] public partial bool IsWatchesVisible { get; set; } = true;

    /// <summary>
    /// Gets or sets the debugger adapter path.
    /// </summary>
    [Reactive]
    public partial string DebuggerAdapterPath { get; set; } = "tools/netcoredbg/netcoredbg/netcoredbg";

    /// <summary>
    /// Gets or sets whether debug tools can be auto-downloaded.
    /// </summary>
    [Reactive]
    public partial bool AutoDownloadTools { get; set; } = true;

    /// <summary>
    /// Gets or sets the program arguments for run/debug.
    /// </summary>
    [Reactive]
    public partial string ProgramArguments { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether debugging should stop at entry.
    /// </summary>
    [Reactive]
    public partial bool DebugStopAtEntry { get; set; }

    [Reactive]
    public partial bool IsRunActive { get; private set; }

    // File Commands
    public ReactiveCommand<RxVoid, RxVoid> NewDocumentCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenDocumentCommand { get; }
    /// <summary>Opens the workspace selection dialog.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenWorkspaceCommand { get; }
    /// <summary>Closes the active file.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseActiveDocumentCommand { get; }
    /// <summary>Closes all files and unloads the workspace.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseWorkspaceCommand { get; }
    public ReactiveCommand<string, RxVoid> OpenPathCommand { get; }
    public ReactiveCommand<IReadOnlyList<string>, RxVoid> OpenPathsCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SaveDocumentCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SaveAllCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ExitCommand { get; }

    // Save behavior commands
    public ReactiveCommand<RxVoid, RxVoid> SetAutoSaveCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SetManualSaveCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SetNoSaveCommand { get; }

    // Theme commands
    public ReactiveCommand<RxVoid, RxVoid> SetThemeDefaultCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SetThemeLightCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SetThemeDarkCommand { get; }

    // Extension-owned non-lifecycle shell command handlers
    private ReactiveCommand<RxVoid, RxVoid> UndoCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> RedoCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> CutCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> CopyCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> PasteCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> DuplicateCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> DeleteCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> SelectAllCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> RenameSymbolCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> FormatDocumentCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> CodeActionsCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> DocumentSymbolsCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> WorkspaceSymbolsCommand { get; }

    // View Commands
    private ReactiveCommand<RxVoid, RxVoid> ToggleBreakpointsCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> ToggleCallStackCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> ToggleLocalsCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> ToggleWatchesCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenExtensionManagerCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ResetLayoutCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SaveLayoutCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SaveLayoutAsCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> DeleteLayoutCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ManageLayoutsCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenCanvasCommand { get; }

    // Help Commands
    public ReactiveCommand<RxVoid, RxVoid> AboutCommand { get; }

    // Previewer Commands
    public ReactiveCommand<RxVoid, RxVoid> StartPreviewerCommand { get; }

    // Extension-owned non-lifecycle shell command handlers
    private ReactiveCommand<RxVoid, RxVoid> StartDebugCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> StopDebugCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> ContinueDebugCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> StepOverCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> StepInCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> StepOutCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> PauseDebugCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> ToggleBreakpointCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> StartRunCommand { get; }
    private ReactiveCommand<RxVoid, RxVoid> StopRunCommand { get; }

    // Terminal Commands
    private ReactiveCommand<RxVoid, RxVoid> NewTerminalCommand { get; }

    // Command palette
    public ReactiveCommand<RxVoid, RxVoid> ShowCommandPaletteCommand { get; }

    /// <summary>
    /// Command to close a specific document (used by tab close button).
    /// </summary>
    public ReactiveCommand<IEditorDocumentViewModel, RxVoid> CloseDocumentCommand { get; }

    public MainWindowViewModel(
        IWorkspaceService? workspaceService = null,
        IWorkspaceInfoUpdater? workspaceInfoUpdater = null,
        ITypeMetadataService? metadataService = null,
        ILanguageIntellisenseRegistry? languageRegistry = null,
        IAnimationPreviewService? animationPreviewService = null,
        IDebuggerServiceRegistry? debuggerRegistry = null,
        IDebugToolInstaller? debugToolInstaller = null,
        IOutputLogSinkAccessor? outputLogSinkAccessor = null,
        IWindow? window = null,
        XamlVisualEditor.Terminal.ITerminalService? terminalService = null,
        ILspSettingsStore? lspSettingsStore = null,
        ICommands? extensionCommands = null,
        ICommandMetadataRegistry? commandMetadata = null,
        IExtensionContributionRegistry? extensionContributionRegistry = null,
        IExtensionViewRegistry? extensionViewRegistry = null,
        IExtensionManager? extensionManager = null,
        ILogger<MainWindowViewModel>? logger = null,
        ILoggerFactory? loggerFactory = null,
        ISystemIconService? systemIconService = null,
        IDotNetCli? dotNetCli = null,
        IControlCatalogUpdater? controlCatalogUpdater = null,
        IClipboardService? clipboardService = null,
        IWorkspaceInfo? themeWorkspaceInfo = null,
        Func<string, IExtensionStorage>? extensionStorageFactory = null,
        string? recentFilesPath = null,
        EditorOptionsStore? editorOptions = null,
        IUserLayoutStore? userLayoutStore = null,
        IEditorThemeService? editorThemes = null,
        IContextKeys? contextKeys = null,
        IBackgroundActivityMonitor? backgroundActivities = null,
        IDocumentOpenResolver? documentOpenResolver = null,
        WorkspaceSessionStore? workspaceSessions = null)
    {
        _recentFilesPath = recentFilesPath;
        _workspaceSessions = workspaceSessions
            ?? new WorkspaceSessionStore(System.IO.Path.Combine(ShellSettingsPaths.GetRoot(), "workspace-sessions.json"));
        _backgroundActivities = backgroundActivities;
        _documentOpenResolver = documentOpenResolver;
        if (_backgroundActivities is not null)
        {
            // Subscribe first: an activity that ends before the state is read below still raises a re-evaluation.
            IDisposable activitySubscription = Observable.FromEventPattern(
                    handler => _backgroundActivities.Changed += handler,
                    handler => _backgroundActivities.Changed -= handler)
                .WitnessOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => OnBackgroundActivitiesChanged());
            _disposables.Add(activitySubscription);
            if (_backgroundActivities.CurrentDescription is { } startupActivity)
            {
                IsStarting = true;
                _activityStatusText = startupActivity;
                _statusText = startupActivity;
            }
        }
        _contextKeys = contextKeys;
        if (_contextKeys is not null)
        {
            _contextKeys.Changed += OnContextKeysChanged;
            _disposables.Add(System.Reactive.Disposables.Disposable.Create(() => _contextKeys.Changed -= OnContextKeysChanged));
        }
        _editorOptions = editorOptions ?? new EditorOptionsStore();
        _editorThemes = editorThemes;
        if (_editorThemes is not null)
        {
            string? themeStatusError = null;
            EventHandler themeChanged = (_, _) =>
            {
                if (_editorThemes.Error is { } error)
                {
                    SetPersistentStatus(error);
                    themeStatusError = error;
                }
                else
                {
                    if (themeStatusError is not null && StatusText == themeStatusError)
                        ShowIdleStatus();
                    themeStatusError = null;
                }
                if (_editorThemes.Selection.ProviderId == "builtin")
                    SelectedThemeVariant = _editorThemes.Selection.ThemeId switch
                    {
                        "light" => ThemeVariantOption.Light, "dark" => ThemeVariantOption.Dark, _ => ThemeVariantOption.Default
                    };
            };
            _editorThemes.Changed += themeChanged;
            _disposables.Add(System.Reactive.Disposables.Disposable.Create(() => _editorThemes.Changed -= themeChanged));
        }
        _userLayoutStore = userLayoutStore ?? UserLayoutStore.CreateDefault();
        _workspaceService = workspaceService;
        _workspaceInfoUpdater = workspaceInfoUpdater;
        _metadataService = metadataService;
        _controlCatalogUpdater = controlCatalogUpdater;
        _clipboardService = clipboardService;
        _dotNetCli = dotNetCli ?? new DotNetCliRunner();
        _languageRegistry = languageRegistry;
        _debuggerRegistry = debuggerRegistry ?? new DebuggerServiceRegistry();
        InitializeDebuggerServices();
        _debugToolInstaller = debugToolInstaller;
        _outputLogSinkAccessor = outputLogSinkAccessor;
        _outputChannel = window?.CreateOutputChannel("Host");
        _terminalService = terminalService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindowViewModel>.Instance;
        _loggerFactory = loggerFactory;
        _extensionCommands = extensionCommands;
        _commandMetadata = commandMetadata;
        _extensionContributions = extensionContributionRegistry;
        _extensionViewRegistry = extensionViewRegistry;
        ExtensionManager = new ExtensionManagerViewModel(
            extensionManager ?? new NullExtensionManager(),
            () => ExtensionPackageOpenInteraction.Handle(RxVoid.Default));
        InitializeThemePackages(extensionManager);
        SolutionExplorer = new SolutionExplorerViewModel(systemIconService, _extensionCommands);
        DocumentTabCommands = new DocumentTabCommands(RevealInFileExplorerAsync, RevealInSolutionExplorer,
            text => RunClipboardEditAsync(() => WriteClipboardAsync(text)));

        if (_extensionContributions is not null)
        {
            _extensionContributions.Changed += OnExtensionContributionsChanged;
        }

        if (_commandMetadata is not null)
        {
            _commandMetadata.Changed += OnCommandMetadataChanged;
        }

        if (_extensionViewRegistry is not null)
        {
            _extensionViewRegistry.Changed += OnExtensionViewsChanged;
        }

        ExtensionToolbarItems.CollectionChanged += (_, _) =>
            this.RaisePropertyChanged(nameof(HasExtensionToolbarItems));
        MainToolbarItems.CollectionChanged += (_, _) =>
            this.RaisePropertyChanged(nameof(HasExtensionToolbarItems));

        LoadTrustedPreviewerRoots();

        InfiniteCanvas = new InfiniteCanvasViewModel(
            _languageRegistry,
            _loggerFactory?.CreateLogger<InfiniteCanvasViewModel>());

        References = new ReferencesViewModel(async item =>
        {
            LanguageLocation location = new()
            {
                FilePath = item.FilePath,
                Range = new LanguageTextRange(
                    new LanguageTextPosition(item.Line, item.Column),
                    new LanguageTextPosition(item.Line, item.Column))
            };
            await NavigateToLocationAsync(location);
        });

        AnimationEditor = new AnimationEditorViewModel(this, animationPreviewService);
        Debugger = new DebuggerViewModel(_debuggerRegistry);
        DebugSettings = new DebugSettingsViewModel(
            _debuggerRegistry,
            _debugToolInstaller,
            () => DebuggerAdapterPath,
            path => DebuggerAdapterPath = path,
            this.WhenAnyValue(x => x.DebuggerAdapterPath),
            () => AutoDownloadTools,
            value => AutoDownloadTools = value,
            ConfirmDebugToolConsentAsync);

        LspSettings = new LspSettingsViewModel(lspSettingsStore);

        if (_outputLogSinkAccessor is not null)
        {
            _outputLogSinkAccessor.Sink = Output;
        }

        DockFactory = new XamlEditorDockFactory(
            this,
            _loggerFactory?.CreateLogger<XamlEditorDockFactory>());
        IRootDock? persistedLayout = DockFactory.LoadLayout(out string? persistedLayoutName);
        LayoutPreset defaultPreset = FindLayoutPreset(LayoutPresetCatalog.DefaultId)!;
        IRootDock layout = persistedLayout ?? DockFactory.CreateDefaultLayout(defaultPreset.IsVisible);
        XamlEditorDockFactory.EnsureLayoutDefaults(layout);
        DockFactory.InitLayout(layout);
        DockFactory.EnsureOwnerReferences(layout);
        DockFactory.ConfigureToolViewModels(layout);
        DockFactory.ConfigureDocumentViewModels(layout);
        DockLayout = layout;
        BeginExtensionViewPlacement(persistedLayout is null ? defaultPreset.VisiblePanelIds : null);
        if (persistedLayout is not null && persistedLayoutName is not null && _userLayoutStore.Exists(persistedLayoutName))
        {
            SetCurrentLayout(persistedLayoutName, isLoaded: true);
        }

        WireDockEvents();


        IObservable<int> activeLine = Observable.Switch(this.WhenAnyValue(x => x.ActiveDocument)
            .Select(doc => doc is null
                ? Observable.Return(1)
                : ObserveDocumentProperty(doc, nameof(IEditorDocumentViewModel.CurrentLine), () => doc.CurrentLine))
            );
        _activeLine = activeLine.ToProperty(this, x => x.ActiveLine);
        _disposables.Add(_activeLine);

        IObservable<int> activeColumn = Observable.Switch(this.WhenAnyValue(x => x.ActiveDocument)
            .Select(doc => doc is null
                ? Observable.Return(1)
                : ObserveDocumentProperty(doc, nameof(IEditorDocumentViewModel.CurrentColumn), () => doc.CurrentColumn))
            );
        _activeColumn = activeColumn.ToProperty(this, x => x.ActiveColumn);
        _disposables.Add(_activeColumn);

        // A designer document shows its code editor except in the design-only view.
        IObservable<object?> activeTextEditorDocument = Observable.Switch(this.WhenAnyValue(x => x.ActiveDocument)
            .Select(document => document is DesignerDocumentViewModel designer
                ? designer.WhenAnyValue(x => x.ViewMode)
                    .Select(mode => mode == DocumentViewMode.Design ? null : (object?)designer.CodeEditor)
                : Observable.Return<object?>(document))
            );
        _activeTextEditorDocument = activeTextEditorDocument.ToProperty(this, x => x.ActiveTextEditorDocument);
        _disposables.Add(_activeTextEditorDocument);
        IDisposable textEditorVisibility = this.WhenAnyValue(x => x.ActiveTextEditorDocument)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(HasVisibleTextEditor)));
        _disposables.Add(textEditorVisibility);

        _dockDocuments.Clear();
        foreach (IEditorDocumentViewModel doc in Documents)
        {
            AddDocumentToDock(doc);
        }

        InfiniteCanvas.UpdateOpenDocuments(Documents);
        Documents.CollectionChanged += (_, _) => InfiniteCanvas.UpdateOpenDocuments(Documents);
        Documents.CollectionChanged += (_, _) => SaveWorkspaceSession();
        _disposables.Add(this.WhenAnyValue(x => x.ActiveDocument).Skip(1).Subscribe(_ => SaveWorkspaceSession()));

        // File commands
        NewDocumentCommand = ReactiveCommand.CreateFromTask(NewDocumentAsync);
        OpenDocumentCommand = ReactiveCommand.CreateFromTask(() => OpenDocumentAsync(openWorkspace: false));
        OpenWorkspaceCommand = ReactiveCommand.CreateFromTask(() => OpenDocumentAsync(openWorkspace: true));
        CloseActiveDocumentCommand = ReactiveCommand.CreateFromTask(
            async () => { if (ActiveDocument is { } document) await ConfirmAndCloseDocumentAsync(document); },
            this.WhenAnyValue(x => x.ActiveDocument).Select(document => document is not null));
        CloseWorkspaceCommand = ReactiveCommand.CreateFromTask(CloseWorkspaceAsync,
            this.WhenAnyValue(x => x.HasWorkspace, x => x.IsWorkspaceLoading, x => x.IsWorkspaceCommandRunning,
                (hasWorkspace, loading, running) => hasWorkspace && !loading && !running));
        _disposables.Add(OpenWorkspaceCommand);
        _disposables.Add(CloseActiveDocumentCommand);
        _disposables.Add(CloseWorkspaceCommand);
        OpenPathCommand = ReactiveCommand.CreateFromTask<string>(OpenFileAsync);
        OpenPathsCommand = ReactiveCommand.CreateFromTask<IReadOnlyList<string>>(OpenDroppedPathsAsync);
        OpenRecentCommand = ReactiveCommand.CreateFromTask<RecentFileEntry>(OpenRecentAsync);
        _disposables.Add(OpenRecentCommand);

        OpenExtensionManagerCommand = ReactiveCommand.CreateFromTask(
            () => ExtensionManagerInteraction.Handle(RxVoid.Default));
        RefreshExtensionContributions();
        UpdateFileNewMenuEntries();
        SyncExtensionDockables();
        if (persistedLayout is null) ArrangePresetLayout(DockLayout, defaultPreset);

        LoadRecentFiles();
        RecentFiles.CollectionChanged += (_, _) =>
        {
            this.RaisePropertyChanged(nameof(RecentWorkspaces));
            this.RaisePropertyChanged(nameof(RecentDocuments));
            this.RaisePropertyChanged(nameof(RecentFolders));
            this.RaisePropertyChanged(nameof(HasRecentWorkspaces));
            this.RaisePropertyChanged(nameof(HasRecentDocuments));
            this.RaisePropertyChanged(nameof(HasRecentFolders));
            this.RaisePropertyChanged(nameof(HasRecentFiles));
            SaveRecentFiles();
        };
        ClearRecentFilesCommand = ReactiveCommand.Create(RecentFiles.Clear, this.WhenAnyValue(x => x.HasRecentFiles));
        ManageRecentFilesCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            using RecentFilesDialogViewModel dialog = new(RecentFiles);
            await RecentFilesInteraction.Handle(dialog);
        });
        _disposables.Add(ClearRecentFilesCommand);
        _disposables.Add(ManageRecentFilesCommand);
        OpenOptionsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            using OptionsDialogViewModel dialog = new(_editorOptions, _editorThemes);
            if (await OptionsInteraction.Handle(dialog))
            {
                foreach (ExtensionTreeViewModel tree in ExtensionViews.OfType<ExtensionTreeViewModel>())
                    tree.SetFoldersBeforeFiles(_editorOptions.Current.FileExplorerFoldersBeforeFiles);
                UpdateWindowTitle();
                StatusText = XamlVisualEditor.Core.Localization.UiStrings.OptionsSaved;
            }
        });
        _disposables.Add(OpenOptionsCommand);

        IObservable<bool> hasActiveDoc = this.WhenAnyValue(x => x.ActiveDocument).Select(d => d is not null);
        IObservable<bool> hasDesignerDoc = this.WhenAnyValue(x => x.ActiveDesignerDocument).Select(d => d is not null);
        IObservable<bool> hasTextDoc = this.WhenAnyValue(x => x.ActiveTextDocument).Select(d => d is not null);
        IObservable<bool> hasEditableDoc = hasDesignerDoc.CombineLatest(hasTextDoc, (designer, text) => designer || text);
        IObservable<bool> hasDesignerSelection = ObserveDesignerDocumentState(
            doc => doc.SelectionSource == SyncSource.CodeEditor
                ? doc.CodeEditor.SelectionLength > 0 : doc.SelectedNodeId is not null,
            doc => doc.WhenAnyValue(x => x.SelectedNodeId, x => x.SelectionSource).Select(_ => Unit.Default),
            doc => doc.CodeEditor.WhenAnyValue(x => x.SelectionLength).Select(_ => Unit.Default));
        IObservable<bool> canUndoDesigner = ObserveDesignerDocumentState(
            doc => doc.SyncEngine.UndoRedo.CanUndo,
            doc => Observable.FromEvent(
                handler => doc.SyncEngine.UndoRedo.StateChanged += handler,
                handler => doc.SyncEngine.UndoRedo.StateChanged -= handler));
        IObservable<bool> canRedoDesigner = ObserveDesignerDocumentState(
            doc => doc.SyncEngine.UndoRedo.CanRedo,
            doc => Observable.FromEvent(
                handler => doc.SyncEngine.UndoRedo.StateChanged += handler,
                handler => doc.SyncEngine.UndoRedo.StateChanged -= handler));
        IObservable<bool> hasTextSelection = ObserveTextDocumentState(
            doc => GetTextSelectionLength(doc) > 0,
            doc => doc.WhenAnyValue(x => x.SelectionStart, x => x.SelectionLength).Select(_ => Unit.Default));
        IObservable<bool> canDeleteText = ObserveTextDocumentState(
            doc =>
            {
                int textLength = doc.Document.TextLength;
                if (GetTextSelectionLength(doc) > 0)
                {
                    return true;
                }

                int caretOffset = Math.Clamp(doc.CaretOffset, 0, textLength);
                return caretOffset < textLength;
            },
            doc => doc.WhenAnyValue(x => x.SelectionStart, x => x.SelectionLength).Select(_ => Unit.Default),
            doc => doc.WhenAnyValue(x => x.CaretOffset).Select(_ => Unit.Default),
            ObserveTextDocumentTextChanges);
        IObservable<bool> canUndoText = ObserveTextDocumentState(
            doc => doc.Document.UndoStack.CanUndo,
            ObserveTextDocumentTextChanges);
        IObservable<bool> canRedoText = ObserveTextDocumentState(
            doc => doc.Document.UndoStack.CanRedo,
            ObserveTextDocumentTextChanges);
        IObservable<bool> hasClipboardText = this.WhenAnyValue(x => x.ClipboardVersion)
            .Select(_ => !string.IsNullOrEmpty(_clipboard))
            .StartWith(!string.IsNullOrEmpty(_clipboard));
        IObservable<bool> canUndoEdit = canUndoDesigner.CombineLatest(canUndoText, (designer, text) => designer || text);
        IObservable<bool> canRedoEdit = canRedoDesigner.CombineLatest(canRedoText, (designer, text) => designer || text);
        IObservable<bool> canCutOrCopy = hasDesignerSelection.CombineLatest(hasTextSelection, (designer, text) => designer || text);
        IObservable<bool> canDeleteEdit = hasDesignerSelection.CombineLatest(canDeleteText, (designer, text) => designer || text);
        IObservable<bool> canPasteEdit = hasEditableDoc.CombineLatest(hasClipboardText,
            (editable, clipboard) => editable && (_clipboardService is not null || clipboard));
        SaveDocumentCommand = ReactiveCommand.CreateFromTask(SaveActiveDocumentAsync, hasActiveDoc);
        SaveAllCommand = ReactiveCommand.CreateFromTask(SaveAllAsync);
        ExitCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                && await ConfirmShutdownAsync())
            {
                desktop.Shutdown();
            }
        });

        SetAutoSaveCommand = ReactiveCommand.Create(() =>
        {
            SaveBehavior = SaveBehavior.AutoSave;
            StatusText = UiStrings.SaveModeAutoStatus;
        });
        SetManualSaveCommand = ReactiveCommand.Create(() =>
        {
            SaveBehavior = SaveBehavior.SaveManually;
            StatusText = UiStrings.SaveModeManualStatus;
        });
        SetNoSaveCommand = ReactiveCommand.Create(() =>
        {
            SaveBehavior = SaveBehavior.NoSaving;
            StatusText = UiStrings.SaveModeDisabledStatus;
        });

        SetThemeDefaultCommand = ReactiveCommand.Create(() =>
        {
            ApplyEditorThemeVariant(ThemeVariantOption.Default);
        });
        SetThemeLightCommand = ReactiveCommand.Create(() =>
        {
            ApplyEditorThemeVariant(ThemeVariantOption.Light);
        });
        SetThemeDarkCommand = ReactiveCommand.Create(() =>
        {
            ApplyEditorThemeVariant(ThemeVariantOption.Dark);
        });

        RenameSymbolCommand = ReactiveCommand.CreateFromTask(RenameSymbolAsync, hasTextDoc);
        FormatDocumentCommand = ReactiveCommand.CreateFromTask(FormatDocumentAsync, hasTextDoc);
        CodeActionsCommand = ReactiveCommand.CreateFromTask(ShowCodeActionsAsync, hasTextDoc);
        DocumentSymbolsCommand = ReactiveCommand.CreateFromTask(ShowDocumentSymbolsAsync, hasTextDoc);
        WorkspaceSymbolsCommand = ReactiveCommand.CreateFromTask(ShowWorkspaceSymbolsAsync, hasTextDoc);

        // Edit commands
        UndoCommand = ReactiveCommand.Create(() => RunDesignerEdit(() =>
        {
            if (ActiveDesignerDocument is not null)
            {
                ActiveDesignerDocument.SyncEngine.Undo();
                return;
            }

                ActiveTextDocument?.Undo();
        }), canUndoEdit);

        RedoCommand = ReactiveCommand.Create(() => RunDesignerEdit(() =>
        {
            if (ActiveDesignerDocument is not null)
            {
                ActiveDesignerDocument.SyncEngine.Redo();
                return;
            }

                ActiveTextDocument?.Redo();
        }), canRedoEdit);

        CutCommand = ReactiveCommand.CreateFromTask(
            () => RunClipboardEditAsync(() => CopySelectionAsync(cut: true)), canCutOrCopy);
        CopyCommand = ReactiveCommand.CreateFromTask(
            () => RunClipboardEditAsync(() => CopySelectionAsync(cut: false)), canCutOrCopy);
        PasteCommand = ReactiveCommand.CreateFromTask(
            () => RunClipboardEditAsync(PasteSelectionAsync), canPasteEdit);
        DuplicateCommand = ReactiveCommand.Create(() => RunDesignerEdit(() =>
        {
            if (ActiveDesignerDocument is not { } document) return;
            DesignerSelectionEditor editor = new(document.SyncEngine, _metadataService, document.DesignSurface.AutoGenerateAutomationIds);
            ApplyDesignerSelection(document, editor.Duplicate(GetSelectedObjects(document)));
        }), hasDesignerSelection);
        DeleteCommand = ReactiveCommand.Create(() => RunDesignerEdit(() =>
        {
            if (ActiveDesignerDocument is { } document)
            {
                if (document.SelectionSource == SyncSource.CodeEditor)
                {
                    int start = Math.Clamp(document.CodeEditor.SelectionStart, 0, document.CodeEditor.Document.TextLength);
                    int length = Math.Clamp(document.CodeEditor.SelectionLength, 0, document.CodeEditor.Document.TextLength - start);
                    if (length > 0) document.CodeEditor.Document.Remove(start, length);
                    document.CodeEditor.SetSelection(start, 0, start);
                    return;
                }
                new DesignerSelectionEditor(document.SyncEngine, _metadataService).Delete(GetSelectedObjects(document));
                ApplyDesignerSelection(document, Array.Empty<MutableAstObjectNode>());
                return;
            }
            if (ActiveTextDocument is { } textDocument) DeleteTextSelectionOrCharacter(textDocument);
        }), canDeleteEdit);
        SelectAllCommand = ReactiveCommand.Create(() => RunDesignerEdit(() =>
        {
            if (ActiveDesignerDocument is { } document)
            {
                if (document.SelectionSource == SyncSource.CodeEditor)
                {
                    document.CodeEditor.SelectAll();
                    return;
                }
                MutableAstObjectNode? selected = GetSelectedObjects(document).FirstOrDefault();
                MutableAstObjectNode? parent = selected is null ? null : LayoutEditService.GetParent(selected);
                parent ??= document.SyncEngine.CurrentDocument?.Root;
                if (parent is not null)
                {
                    MutableAstObjectNode[] children = new LayoutEditService(_metadataService).GetChildren(parent).OfType<MutableAstObjectNode>().ToArray();
                    document.SetSelectedNode(children.FirstOrDefault()?.Id, SyncSource.DesignSurface);
                    document.DesignSurface.SelectNodesFromSync(children.Select(node => node.Id));
                }
                return;
            }
            SelectAllText(ActiveTextDocument);
        }), hasEditableDoc);

        // View commands
        ToggleBreakpointsCommand = ReactiveCommand.Create(() =>
        {
            IsBreakpointsVisible = !IsDockableShown("Breakpoints");
            SetDockableVisibility("Breakpoints", IsBreakpointsVisible);
        });
        ToggleCallStackCommand = ReactiveCommand.Create(() =>
        {
            IsCallStackVisible = !IsDockableShown("CallStack");
            SetDockableVisibility("CallStack", IsCallStackVisible);
        });
        ToggleLocalsCommand = ReactiveCommand.Create(() =>
        {
            IsLocalsVisible = !IsDockableShown("Locals");
            SetDockableVisibility("Locals", IsLocalsVisible);
        });
        ToggleWatchesCommand = ReactiveCommand.Create(() =>
        {
            IsWatchesVisible = !IsDockableShown("Watches");
            SetDockableVisibility("Watches", IsWatchesVisible);
        });
        ResetLayoutCommand = ReactiveCommand.Create(ResetLayout);
        SaveLayoutCommand = ReactiveCommand.CreateFromTask(() => SaveUserLayoutAsync(promptForName: false));
        SaveLayoutAsCommand = ReactiveCommand.CreateFromTask(() => SaveUserLayoutAsync(promptForName: true));
        DeleteLayoutCommand = ReactiveCommand.CreateFromTask(
            DeleteCurrentLayoutAsync,
            this.WhenAnyValue(x => x.CurrentLayoutName).Select(name => name is not null));
        ManageLayoutsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            using ManageLayoutsDialogViewModel dialog = new(_userLayoutStore, OnUserLayoutRenamed, OnUserLayoutDeleted);
            await ManageLayoutsInteraction.Handle(dialog);
        });
        _disposables.Add(ManageLayoutsCommand);
        IDisposable deleteLayoutHeaderSubscription = this.WhenAnyValue(x => x.CurrentLayoutName)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(DeleteLayoutMenuHeader)));
        _disposables.Add(deleteLayoutHeaderSubscription);
        SavedLayoutMenuItems.CollectionChanged += (_, _) =>
            this.RaisePropertyChanged(nameof(HasSavedLayouts));
        InitializeLayoutPresetMenuItems();
        RefreshSavedLayoutMenuItems();
        OpenCanvasCommand = ReactiveCommand.Create(ShowCanvasDocument);

        // Help commands
        AboutCommand = ReactiveCommand.Create(() =>
        {
            StatusText = UiStrings.AboutSummary;
        });

        IObservable<bool> hasWorkspace = this.WhenAnyValue(x => x.HasWorkspace);
        IObservable<DebugSessionState> debugState = this.WhenAnyValue(x => x.Debugger.State);
        IObservable<bool> hasDebuggerService = this.WhenAnyValue(x => x.Debugger.HasDebuggerService);
        IObservable<bool> canStartDebug = hasWorkspace
            .CombineLatest(hasDebuggerService, debugState, this.WhenAnyValue(x => x.IsRunActive),
                (hasWs, hasService, state, isRunActive) =>
                    hasWs && hasService && !isRunActive && IsDebugIdle(state));
        IObservable<bool> canStopDebug = debugState.Select(state =>
            state == DebugSessionState.Initializing || state == DebugSessionState.Running || state == DebugSessionState.Paused);
        IObservable<bool> canContinue = debugState.Select(state => state == DebugSessionState.Paused);
        IObservable<bool> canStep = canContinue;
        IObservable<bool> canPause = debugState.Select(state => state == DebugSessionState.Running);
        IObservable<bool> canStartRun = hasWorkspace
            .CombineLatest(debugState, (hasWs, state) => hasWs && IsDebugIdle(state));

        StartPreviewerCommand = ReactiveCommand.CreateFromTask(
            StartPreviewerForActiveDocumentAsync,
            Observable.Switch(this.WhenAnyValue(viewModel => viewModel.ActiveDesignerDocument)
                .Select(document => document is null ? Observable.Return(false) : document.WhenAnyValue(value => value.IsLoaded))
                ).CombineLatest(hasWorkspace, (loaded, hasWs) => loaded && hasWs));

        StartDebugCommand = ReactiveCommand.CreateFromTask(StartDebuggingAsync, canStartDebug);
        StopDebugCommand = ReactiveCommand.CreateFromTask(StopDebuggingAsync, canStopDebug);
        ContinueDebugCommand = ReactiveCommand.CreateFromTask(() => Debugger.ContinueAsync(), canContinue);
        StepOverCommand = ReactiveCommand.CreateFromTask(() => Debugger.StepOverAsync(), canStep);
        StepInCommand = ReactiveCommand.CreateFromTask(() => Debugger.StepInAsync(), canStep);
        StepOutCommand = ReactiveCommand.CreateFromTask(() => Debugger.StepOutAsync(), canStep);
        PauseDebugCommand = ReactiveCommand.CreateFromTask(() => Debugger.PauseAsync(), canPause);
        ToggleBreakpointCommand = ReactiveCommand.Create(ToggleBreakpointAtCaret, hasActiveDoc);
        StartRunCommand = ReactiveCommand.CreateFromTask(StartRunAsync, canStartRun);
        StopRunCommand = ReactiveCommand.CreateFromTask(StopRunAsync, this.WhenAnyValue(x => x.IsRunActive));

        NewTerminalCommand = ReactiveCommand.Create(CreateTerminalSession);

        ShowCommandPaletteCommand = ReactiveCommand.CreateFromTask(ShowCommandPaletteAsync);
        RefreshExtensionCommandEnablement();
        IDisposable extensionCommandEnablementSubscription = Observable.Merge(
                this.WhenAnyValue(x => x.ActiveDocument).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.ActiveDesignerDocument).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.ActiveTextDocument).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.HasWorkspace).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.IsWorkspaceLoading).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.IsWorkspaceCommandRunning).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.CanNavigateBack).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.CanNavigateForward).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.IsRunActive).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.Debugger.HasDebuggerService).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.Debugger.State).Select(_ => Unit.Default))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshExtensionCommandEnablement());
        _disposables.Add(extensionCommandEnablementSubscription);

        // Close document command (used by tab close buttons)
        CloseDocumentCommand = ReactiveCommand.CreateFromTask<IEditorDocumentViewModel>(ConfirmAndCloseDocumentAsync);
        ReportCommandFailures(UiStrings.DocumentOpenFailed,
            OpenDocumentCommand, OpenWorkspaceCommand, OpenPathCommand, OpenPathsCommand, OpenRecentCommand);
        ReportCommandFailures(UiStrings.CommandFailed,
            NewDocumentCommand, CloseActiveDocumentCommand, CloseWorkspaceCommand, CloseDocumentCommand,
            SaveDocumentCommand, SaveAllCommand, ExitCommand, OpenExtensionManagerCommand, ClearRecentFilesCommand,
            ManageRecentFilesCommand, OpenOptionsCommand, SetThemeDefaultCommand, SetThemeLightCommand,
            SetThemeDarkCommand, RenameSymbolCommand, FormatDocumentCommand, CodeActionsCommand,
            DocumentSymbolsCommand, WorkspaceSymbolsCommand, UndoCommand, RedoCommand, CutCommand, CopyCommand,
            PasteCommand, DuplicateCommand, DeleteCommand, SelectAllCommand, ToggleBreakpointsCommand,
            ToggleCallStackCommand, ToggleLocalsCommand, ToggleWatchesCommand, ResetLayoutCommand, SaveLayoutCommand,
            SaveLayoutAsCommand, DeleteLayoutCommand, ManageLayoutsCommand, OpenCanvasCommand, StartPreviewerCommand,
            StartDebugCommand, StopDebugCommand, ContinueDebugCommand, StepOverCommand, StepInCommand,
            StepOutCommand, PauseDebugCommand, ToggleBreakpointCommand, StartRunCommand, StopRunCommand,
            NewTerminalCommand, ShowCommandPaletteCommand);

        // Update trees when active document changes
        IDisposable activeDocumentTreesSubscription = this.WhenAnyValue(x => x.ActiveDesignerDocument)
            .Where(doc => doc is null || !doc.IsDisposed)
            .Subscribe(doc =>
            {
                ConfigurePreviewThemes(doc);
                if (!IsWorkspaceLoading) RefreshControlCatalog();
                UpdateTrees(doc);
            });
        _disposables.Add(activeDocumentTreesSubscription);

        IDisposable activeDocumentDockSubscription = this.WhenAnyValue(x => x.ActiveDocument)
            .Where(doc => doc is not null)
            .Subscribe(doc => SetActiveDockDocument(doc!));
        _disposables.Add(activeDocumentDockSubscription);

        // Refresh trees on sync events from active document (Switch unsubscribes from previous)
        IDisposable activeDocumentSyncSubscription = Observable.Switch(this.WhenAnyValue(x => x.ActiveDesignerDocument)
            .Where(d => d is not null && !d.IsDisposed)
            .Select(d => d!.SyncEngine.SyncEvents.Select(_ => d))
            )
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(d => UpdateTrees(d));
        _disposables.Add(activeDocumentSyncSubscription);

        InitializeThemePreferences(themeWorkspaceInfo ?? workspaceInfoUpdater as IWorkspaceInfo,
            extensionStorageFactory?.Invoke("xve.designerThemes"));

        IDisposable previewerSessionSubscription = this.WhenAnyValue(x => x.ActiveDesignerDocument)
            .Subscribe(doc =>
            {
                if (doc is null || doc.IsDisposed)
                {
                    return;
                }

                // Without a host the document keeps its state, so a previewer that exited stays reported as exited.
                if (_previewerLaunchService.TryGetSession(doc.FilePath, out PreviewerTcpSession? session))
                {
                    doc.PreviewerSession = session;
                    doc.PreviewerState = PreviewerState.Running;
                }
                else
                {
                    doc.PreviewerSession = null;
                }
                TrackPreviewerActivation(doc);
            });
        _disposables.Add(previewerSessionSubscription);

        _previewerLaunchService.PreviewerErrorReceived += OnPreviewerErrorReceived;
        _previewerLaunchService.PreviewerExited += OnPreviewerExited;
        _disposables.Add(Disposable.Create(() =>
        {
            _previewerLaunchService.PreviewerErrorReceived -= OnPreviewerErrorReceived;
            _previewerLaunchService.PreviewerExited -= OnPreviewerExited;
        }));

        Debugger.DebugOutputReceived += OnDebugOutputReceived;
        Debugger.DebugStopped += OnDebugStopped;
        Debugger.DebugContinued += OnDebugContinued;
        _disposables.Add(Disposable.Create(() =>
        {
            Debugger.DebugOutputReceived -= OnDebugOutputReceived;
            Debugger.DebugStopped -= OnDebugStopped;
            Debugger.DebugContinued -= OnDebugContinued;
        }));

        IDisposable executionFrameSubscription = Debugger.CallStack.WhenAnyValue(x => x.SelectedFrame)
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(UpdateExecutionLocationFromFrame);
        _disposables.Add(executionFrameSubscription);

        IDisposable activeDocumentDiagnosticsSubscription = Observable.Switch(this.WhenAnyValue(x => x.ActiveDesignerDocument)
            .Select(doc => doc is null
                ? Observable.Return<(DesignerDocumentViewModel? Doc, IReadOnlyList<XamlDiagnostic> Diags)>(
                    (null, Array.Empty<XamlDiagnostic>()))
                : doc.SyncEngine.SyncEvents.Select(e =>
                    (Doc: (DesignerDocumentViewModel?)doc,
                        Diags: (IReadOnlyList<XamlDiagnostic>)(e.Diagnostics ?? Array.Empty<XamlDiagnostic>()))))
            )
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(result =>
            {
                string? filePath = result.Doc?.FilePath;
                Output.ReplaceDiagnostics(result.Diags, filePath);
                LogDiagnosticsSummary(result.Diags);
            });
        _disposables.Add(activeDocumentDiagnosticsSubscription);

        IDisposable activeTextDiagnosticsSubscription = Observable.Switch(this.WhenAnyValue(x => x.ActiveTextDocument)
            .Select(doc => doc is null
                ? Observable.Empty<IReadOnlyList<LanguageDiagnostic>>()
                : Observable.FromEventPattern<System.Collections.Specialized.NotifyCollectionChangedEventHandler,
                        System.Collections.Specialized.NotifyCollectionChangedEventArgs>(
                        h => doc.Diagnostics.CollectionChanged += h,
                        h => doc.Diagnostics.CollectionChanged -= h)
                    .Select(_ => (IReadOnlyList<LanguageDiagnostic>)doc.Diagnostics.ToList())
                    .StartWith((IReadOnlyList<LanguageDiagnostic>)doc.Diagnostics.ToList()))
            )
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(diags => Output.ReplaceLanguageDiagnostics(diags));
        _disposables.Add(activeTextDiagnosticsSubscription);

        IDisposable outputSelectionSubscription = this.WhenAnyValue(x => x.Output.SelectedMessage)
            .Where(msg => msg is not null)
            .Subscribe(msg => _ = NavigateToOutputMessageAsync(msg));
        _disposables.Add(outputSelectionSubscription);

        IDisposable saveBehaviorSubscription = this.WhenAnyValue(x => x.SaveBehavior)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsAutoSaveSelected));
                this.RaisePropertyChanged(nameof(IsManualSaveSelected));
                this.RaisePropertyChanged(nameof(IsNoSaveSelected));
            });
        _disposables.Add(saveBehaviorSubscription);

        IDisposable themeVariantSubscription = this.WhenAnyValue(x => x.SelectedThemeVariant)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsThemeDefaultSelected));
                this.RaisePropertyChanged(nameof(IsThemeLightSelected));
                this.RaisePropertyChanged(nameof(IsThemeDarkSelected));
            });
        _disposables.Add(themeVariantSubscription);

        // Sync tree selection when active document selection changes
        IDisposable selectionSyncSubscription = Observable.Switch(this.WhenAnyValue(x => x.ActiveDesignerDocument)
            .Select(doc => doc is null || doc.IsDisposed
                ? Observable.Return<Guid?>(null)
                : doc.WhenAnyValue(d => d.SelectedNodeId))
            )
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(id => ApplySelectionToTrees(id));
        _disposables.Add(selectionSyncSubscription);

        // Sync grid selections back to the active document
        IDisposable visualTreeSelectionSubscription = this.WhenAnyValue(x => x.VisualTree.SelectedNode)
            .CombineLatest(this.WhenAnyValue(x => x.ActiveDesignerDocument), (node, doc) => (node, doc))
            .Where(t => t.doc is not null && !t.doc.IsDisposed && !_suppressTreeSelectionSync)
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Where(_ => !AreExtensionTreePanelsVisible())
            .Subscribe(t => t.doc!.SetSelectedNode(t.node?.AstNodeId, SyncSource.TreeView));
        _disposables.Add(visualTreeSelectionSubscription);

        IDisposable logicalTreeSelectionSubscription = this.WhenAnyValue(x => x.LogicalTree.SelectedNode)
            .CombineLatest(this.WhenAnyValue(x => x.ActiveDesignerDocument), (node, doc) => (node, doc))
            .Where(t => t.doc is not null && !t.doc.IsDisposed && !_suppressTreeSelectionSync)
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Where(_ => !AreExtensionTreePanelsVisible())
            .Subscribe(t => t.doc!.SetSelectedNode(t.node?.AstNodeId, SyncSource.TreeView));
        _disposables.Add(logicalTreeSelectionSubscription);

        // The window title follows the active document and its path, which changes with "Save As".
        IDisposable titleUpdateSubscription = Observable.Switch(this.WhenAnyValue(x => x.ActiveDocument)
            .Select(doc => doc is INotifyPropertyChanged notifying
                ? Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                        handler => notifying.PropertyChanged += handler,
                        handler => notifying.PropertyChanged -= handler)
                    .Where(change => change.EventArgs.PropertyName is nameof(IEditorDocumentViewModel.FilePath)
                        or nameof(IEditorDocumentViewModel.FileName))
                    .Select(_ => Unit.Default)
                    .StartWith(Unit.Default)
                : Observable.Return(Unit.Default))
            )
            .Subscribe(_ => UpdateWindowTitle());
        _disposables.Add(titleUpdateSubscription);

        SolutionExplorer.FileOpenRequested += path => { _ = OpenFromSolutionExplorerAsync(path); };

        IDisposable activeDocumentTypeSubscription = this.WhenAnyValue(x => x.ActiveDocument)
            .Subscribe(doc =>
            {
                ActiveDesignerDocument = doc is DesignerDocumentViewModel designer && !designer.IsDisposed
                    ? designer
                    : null;
                ActiveTextDocument = doc is TextDocumentViewModel text && !text.IsDisposed
                    ? text
                    : null;
            });
        _disposables.Add(activeDocumentTypeSubscription);
    }

    private Task ExecuteReactiveCommandAsync(
        ReactiveCommand<RxVoid, RxVoid> reactiveCommand,
        CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return ExecuteReactiveCommandCoreAsync(reactiveCommand, cancellationToken);
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await ExecuteReactiveCommandCoreAsync(reactiveCommand, cancellationToken).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, DispatcherPriority.Normal);
        return completion.Task;
    }

    private static Task ExecuteReactiveCommandCoreAsync(
        ReactiveCommand<RxVoid, RxVoid> reactiveCommand,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reactiveCommand is ICommand commandBinding && !commandBinding.CanExecute(null))
        {
            return Task.CompletedTask;
        }

        return reactiveCommand.Execute().ToTask(cancellationToken);
    }

    private static IObservable<int> ObserveDocumentProperty(
        IEditorDocumentViewModel doc,
        string propertyName,
        Func<int> valueProvider)
    {
        if (doc is not INotifyPropertyChanged notifying)
        {
            return Observable.Return(valueProvider());
        }

        return Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => notifying.PropertyChanged += h,
                h => notifying.PropertyChanged -= h)
            .Where(e => string.IsNullOrEmpty(e.EventArgs.PropertyName) || e.EventArgs.PropertyName == propertyName)
            .Select(_ => valueProvider())
            .StartWith(valueProvider());
    }

    private IObservable<bool> ObserveDesignerDocumentState(
        Func<DesignerDocumentViewModel, bool> evaluator,
        params Func<DesignerDocumentViewModel, IObservable<Unit>>[] changeSources)
    {
        return Observable.Switch(this.WhenAnyValue(x => x.ActiveDesignerDocument)
            .Select(doc => doc is null
                ? Observable.Return(false)
                : ObserveDocumentState(doc, evaluator, changeSources))
            );
    }

    private IObservable<bool> ObserveTextDocumentState(
        Func<TextDocumentViewModel, bool> evaluator,
        params Func<TextDocumentViewModel, IObservable<Unit>>[] changeSources)
    {
        return Observable.Switch(this.WhenAnyValue(x => x.ActiveTextDocument)
            .Select(doc => doc is null
                ? Observable.Return(false)
                : ObserveDocumentState(doc, evaluator, changeSources))
            );
    }

    private static IObservable<bool> ObserveDocumentState<TDocument>(
        TDocument document,
        Func<TDocument, bool> evaluator,
        params Func<TDocument, IObservable<Unit>>[] changeSources)
    {
        if (changeSources.Length == 0)
        {
            return Observable.Return(evaluator(document));
        }

        return Observable.Merge(changeSources.Select(source => source(document)))
            .Select(_ => evaluator(document))
            .StartWith(evaluator(document));
    }

    private static IObservable<Unit> ObserveTextDocumentTextChanges(TextDocumentViewModel document)
    {
        return Observable.FromEventPattern<EventHandler, EventArgs>(
                h => document.Document.TextChanged += h,
                h => document.Document.TextChanged -= h)
            .Select(_ => Unit.Default);
    }

    private void ConfigureDesignerCommands(DesignerDocumentViewModel document)
    {
        document.DesignSurface.EditorThemeService = _editorThemes;
        document.DesignSurface.ClipboardService = _clipboardService;
        _editorOptions.Current.DesignerViewport.ApplyTo(document.DesignSurface);
        document.DesignSurface.PropertyChanged += (_, args) => OnDesignerViewportChanged(document, args.PropertyName);
        document.DesignSurface.CutCommand = CutCommand;
        document.DesignSurface.CopyCommand = CopyCommand;
        document.DesignSurface.PasteCommand = PasteCommand;
        document.DesignSurface.DuplicateCommand = DuplicateCommand;
        document.DesignSurface.DeleteSelectedCommand = DeleteCommand;
        document.DesignSurface.SelectAllCommand = SelectAllCommand;
        IObservable<bool> hasMultipleSelection = Observable.FromEvent<IReadOnlyList<IDesignItem>>(
            handler => document.DesignSurface.Selection.SelectionChanged += handler,
            handler => document.DesignSurface.Selection.SelectionChanged -= handler)
            .Select(items => items.Count >= 2).StartWith(false);
        foreach (DesignerLayoutOperation operation in Enum.GetValues<DesignerLayoutOperation>())
        {
            string title = DesignerLayoutEditor.Title(operation);
            ReactiveCommand<RxVoid, RxVoid> command = ReactiveCommand.Create(() => RunDesignerEdit(() =>
            {
                new DesignerLayoutEditor(document).Apply(operation);
                StatusText = title;
            }), hasMultipleSelection);
            document.DesignSurface.LayoutActions.Add(new DesignerLayoutAction(title,
                "Canvas uses the primary selection or selection extent. Grid uses its cells; existing definitions remain unchanged. StackPanel distribution changes shared spacing and requires all children.", command)
            {
                Icon = Avalonia.Media.Geometry.Parse(DesignerLayoutEditor.IconData(operation))
            });
        }
    }

    // Stores a changed grid, ruler or coordinate strip setting and applies it to the other designer documents.
    private void OnDesignerViewportChanged(DesignerDocumentViewModel source, string? propertyName)
    {
        if (_applyingDesignerViewport || propertyName is not (nameof(DesignSurfaceViewModel.ShowGrid)
            or nameof(DesignSurfaceViewModel.SnapToGrid) or nameof(DesignSurfaceViewModel.GridSnapSize)
            or nameof(DesignSurfaceViewModel.ShowRulers) or nameof(DesignSurfaceViewModel.ShowCoordinateStrip)))
        {
            return;
        }

        DesignerViewportOptions options = DesignerViewportOptions.Normalize(DesignerViewportOptions.From(source.DesignSurface));
        if (options == _editorOptions.Current.DesignerViewport)
        {
            return;
        }

        _applyingDesignerViewport = true;
        try
        {
            foreach (DesignerDocumentViewModel document in Documents.OfType<DesignerDocumentViewModel>())
            {
                if (!ReferenceEquals(document, source)) options.ApplyTo(document.DesignSurface);
            }

            _editorOptions.Save(_editorOptions.Current with { DesignerViewport = options });
        }
        catch (Exception error) { SetPersistentStatus(string.Format(UiStrings.OptionsSaveError, error.Message)); }
        finally { _applyingDesignerViewport = false; }
    }

    private static IReadOnlyList<MutableAstObjectNode> GetSelectedObjects(DesignerDocumentViewModel document)
    {
        MutableAstObjectNode[] selected = document.DesignSurface.Selection.SelectedItems.OfType<DesignItem>()
            .Select(item => document.NodeMap.FindById(item.AstNodeId)).OfType<MutableAstObjectNode>().ToArray();
        if (selected.Length > 0) return selected;
        return document.SelectedNodeId is { } id && document.NodeMap.FindById(id) is MutableAstObjectNode node
            ? new[] { node } : Array.Empty<MutableAstObjectNode>();
    }

    private static void ApplyDesignerSelection(DesignerDocumentViewModel document, IReadOnlyList<MutableAstObjectNode> nodes)
    {
        document.SetSelectedNode(nodes.FirstOrDefault()?.Id, SyncSource.DesignSurface);
        document.DesignSurface.RequestSelection(nodes.Select(node => node.Id));
    }

    private void RunDesignerEdit(Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            SetPersistentStatus(exception.Message);
            _logger.LogWarning(exception, "Designer edit failed");
        }
    }

    private async Task RevealInFileExplorerAsync(string filePath)
    {
        const string fileExplorerViewId = "fileExplorer.panel";
        ActivateExtensionView(fileExplorerViewId);
        ExtensionTreeViewModel? fileExplorer = ExtensionViews.OfType<ExtensionTreeViewModel>()
            .FirstOrDefault(view => view.ViewId == fileExplorerViewId);
        if (fileExplorer is null || !await fileExplorer.RevealPathAsync(filePath, CancellationToken.None))
            SetPersistentStatus(string.Format(UiStrings.FileNotInFileExplorer, System.IO.Path.GetFileName(filePath)));
    }

    private void RevealInSolutionExplorer(string filePath)
    {
        ActivateExtensionView("solutionExplorer.panel");
        if (!SolutionExplorer.RevealFile(filePath))
            SetPersistentStatus(string.Format(UiStrings.FileNotInSolutionExplorer, System.IO.Path.GetFileName(filePath)));
    }

    private async Task RunClipboardEditAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception)
        {
            SetPersistentStatus(exception.Message);
            _logger.LogWarning(exception, "Clipboard edit failed");
        }
    }

    private async Task WriteClipboardAsync(string text)
    {
        if (_clipboardService is not null) await _clipboardService.SetTextAsync(text);
        SetClipboardText(text);
    }

    private async Task CopySelectionAsync(bool cut)
    {
        DesignerDocumentViewModel? designer = ActiveDesignerDocument;
        if (designer is not null && designer.SelectionSource != SyncSource.CodeEditor)
        {
            DesignerSelectionEditor editor = new(designer.SyncEngine, _metadataService);
            IReadOnlyList<MutableAstObjectNode> nodes = editor.SelectionRoots(GetSelectedObjects(designer));
            if (nodes.Count == 0) return;
            if (cut && nodes.Any(node => LayoutEditService.GetParent(node) is null))
                throw new InvalidOperationException(UiStrings.CannotCutRoot);
            string? source = designer.SyncEngine.CurrentText;
            await WriteClipboardAsync(editor.Copy(nodes));
            if (!cut) return;
            if (!ReferenceEquals(ActiveDesignerDocument, designer) || designer.SyncEngine.CurrentText != source
                || designer.SelectionSource == SyncSource.CodeEditor
                || !editor.SelectionRoots(GetSelectedObjects(designer)).Select(node => node.Id).SequenceEqual(nodes.Select(node => node.Id)))
                throw new InvalidOperationException(UiStrings.CutSelectionChanged);
            editor.Delete(nodes);
            ApplyDesignerSelection(designer, Array.Empty<MutableAstObjectNode>());
            return;
        }

        TextDocumentViewModel? text = ActiveTextDocument;
        var buffer = designer?.CodeEditor.Document ?? text?.Document;
        if (buffer is null) return;
        int start = Math.Clamp(designer?.CodeEditor.SelectionStart ?? text!.SelectionStart, 0, buffer.TextLength);
        int length = Math.Clamp(designer?.CodeEditor.SelectionLength ?? text!.SelectionLength, 0, buffer.TextLength - start);
        if (length == 0) return;
        string original = buffer.Text;
        var active = ActiveDocument;
        await WriteClipboardAsync(buffer.GetText(start, length));
        if (!cut) return;
        if (!ReferenceEquals(ActiveDocument, active) || buffer.Text != original
            || (designer?.CodeEditor.SelectionStart ?? text!.SelectionStart) != start
            || (designer?.CodeEditor.SelectionLength ?? text!.SelectionLength) != length
            || designer is not null && designer.SelectionSource != SyncSource.CodeEditor)
            throw new InvalidOperationException(UiStrings.CutSelectionChanged);
        buffer.Remove(start, length);
        if (designer is not null)
        {
            designer.CodeEditor.SetSelection(start, 0, start);
        }
        else
        {
            text!.SetSelection(start, 0, start);
        }
    }

    private async Task PasteSelectionAsync()
    {
        var active = ActiveDocument;
        DesignerDocumentViewModel? designer = ActiveDesignerDocument;
        TextDocumentViewModel? text = ActiveTextDocument;
        bool visual = designer is not null && designer.SelectionSource != SyncSource.CodeEditor;
        var buffer = designer?.CodeEditor.Document ?? text?.Document;
        if (buffer is null) return;
        string original = buffer.Text;
        int start = Math.Clamp(designer?.CodeEditor.SelectionStart ?? text!.SelectionStart, 0, buffer.TextLength);
        int length = Math.Clamp(designer?.CodeEditor.SelectionLength ?? text!.SelectionLength, 0, buffer.TextLength - start);
        Guid? selectedId = designer?.SelectedNodeId;
        string? clipboard = _clipboardService is null ? _clipboard : await _clipboardService.GetTextAsync();
        if (string.IsNullOrEmpty(clipboard)) return;
        if (!ReferenceEquals(ActiveDocument, active) || buffer.Text != original
            || visual != (designer is not null && designer.SelectionSource != SyncSource.CodeEditor)
            || visual && designer!.SelectedNodeId != selectedId
            || !visual && ((designer?.CodeEditor.SelectionStart ?? text!.SelectionStart) != start
                || (designer?.CodeEditor.SelectionLength ?? text!.SelectionLength) != length))
            throw new InvalidOperationException(UiStrings.PasteSelectionChanged);
        if (visual)
        {
            MutableAstObjectNode? selected = selectedId is { } id ? designer!.NodeMap.FindById(id) as MutableAstObjectNode : null;
            DesignerSelectionEditor editor = new(designer!.SyncEngine, _metadataService, designer.DesignSurface.AutoGenerateAutomationIds);
            ApplyDesignerSelection(designer, editor.Paste(clipboard, selected));
        }
        else if (designer is not null)
        {
            buffer.Replace(start, length, clipboard);
            designer.CodeEditor.SetSelection(start + clipboard.Length, 0, start + clipboard.Length);
        }
        else ReplaceTextSelection(text!, clipboard);
    }

    private void SetClipboardText(string? text)
    {
        if (string.Equals(_clipboard, text, StringComparison.Ordinal))
        {
            return;
        }

        _clipboard = text;
        ClipboardVersion++;
        this.RaisePropertyChanged(nameof(ClipboardVersion));
        RefreshExtensionCommandEnablement();
    }

    private static int GetTextSelectionLength(TextDocumentViewModel document)
    {
        int start = Math.Clamp(document.SelectionStart, 0, document.Document.TextLength);
        return Math.Clamp(document.SelectionLength, 0, document.Document.TextLength - start);
    }

    private static bool TryGetSelectedText(TextDocumentViewModel document, out string text)
    {
        int start = Math.Clamp(document.SelectionStart, 0, document.Document.TextLength);
        int length = GetTextSelectionLength(document);
        if (length == 0)
        {
            text = string.Empty;
            return false;
        }

        text = document.Document.GetText(start, length);
        return true;
    }

    private static void ReplaceTextSelection(TextDocumentViewModel document, string replacement)
    {
        int textLength = document.Document.TextLength;
        int length = GetTextSelectionLength(document);
        int start = length > 0
            ? Math.Clamp(document.SelectionStart, 0, textLength)
            : Math.Clamp(document.CaretOffset, 0, textLength);

        document.Document.Replace(start, length, replacement);

        int nextOffset = Math.Clamp(start + replacement.Length, 0, document.Document.TextLength);
        document.SetSelection(nextOffset, 0, nextOffset);
    }

    private static void DeleteTextSelectionOrCharacter(TextDocumentViewModel document)
    {
        int length = GetTextSelectionLength(document);
        if (length > 0)
        {
            ReplaceTextSelection(document, string.Empty);
            return;
        }

        int offset = Math.Clamp(document.CaretOffset, 0, document.Document.TextLength);
        if (offset >= document.Document.TextLength)
        {
            return;
        }

        document.Document.Remove(offset, 1);
        document.SetSelection(offset, 0, offset);
    }

    private static void SelectAllText(TextDocumentViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        document.SetSelection(0, document.Document.TextLength, document.Document.TextLength);
    }

    private async System.Threading.Tasks.Task NewDocumentAsync()
    {
        string tempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"Untitled-{Documents.Count + 1}.axaml");

        // Create a basic XAML file
        string defaultXaml = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         x:Class="NewDocument.MainView"
                         Width="400" Height="300">
                <Grid RowDefinitions="Auto,*,Auto">
                    <TextBlock Grid.Row="0"
                               Text="Hello, Avalonia!"
                               FontSize="24"
                               FontWeight="Bold"
                               HorizontalAlignment="Center"
                               Margin="0,16,0,8" />
                    <StackPanel Grid.Row="1"
                                Spacing="8"
                                HorizontalAlignment="Center"
                                VerticalAlignment="Center">
                        <TextBox Width="200" PlaceholderText="Enter text here..." />
                        <Button Content="Click Me"
                                HorizontalAlignment="Center" />
                    </StackPanel>
                    <TextBlock Grid.Row="2"
                               Text="Status: Ready"
                               FontSize="12"
                               Margin="8"
                               Opacity="0.6" />
                </Grid>
            </UserControl>
            """;

        await System.IO.File.WriteAllTextAsync(tempPath, defaultXaml);

        DesignerDocumentViewModel doc = new(
            tempPath,
            _metadataService,
            () => _workspace,
            OpenFileAsync,
            _loggerFactory?.CreateLogger<DesignerDocumentViewModel>(),
            _loggerFactory,
            _languageRegistry,
            StartPreviewerForDocumentAsync,
            UpdatePreviewerForDocumentAsync);
        doc.Breakpoints = Breakpoints;
        ConfigureDesignerCommands(doc);
        Documents.Add(doc);
        ActiveDocument = doc;
        AddDocumentToDock(doc);
        AttachAutoSave(doc);

        try
        {
            await doc.LoadAsync();
            StatusText = string.Format(UiStrings.DocumentCreated, doc.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to load new document: {Message}", ex.Message);
            SetPersistentStatus(string.Format(UiStrings.DocumentCreateFailed, ex.Message));
        }
    }

    private void ApplyEditorThemeVariant(ThemeVariantOption option)
    {
        EditorThemeSelection selection = new("builtin", option switch
        {
            ThemeVariantOption.Light => "light", ThemeVariantOption.Dark => "dark", _ => "system"
        });
        try
        {
            _editorThemes?.Apply(selection);
            _editorOptions.Save(_editorOptions.Current with { EditorTheme = selection });
            SelectedThemeVariant = option;
            StatusText = UiStrings.OptionsSaved;
        }
        catch (Exception error) { SetPersistentStatus(string.Format(UiStrings.OptionsSaveError, error.Message)); }
    }

    private async System.Threading.Tasks.Task OpenDocumentAsync(bool openWorkspace)
    {
        try
        {
            if (openWorkspace)
            {
                WorkspaceOpenRequest? request = await OpenWorkspaceInteraction.Handle(RxVoid.Default);
                if (request is null) return;
                await LoadWorkspaceAsync(request.FilePath);
                if (HasWorkspace && FileSystemPathComparison.Equals(_workspacePath, request.FilePath))
                    AddRecentFile(request.FilePath, RecentEntryKind.Workspace);
                return;
            }
            string? filePath = await OpenFileInteraction.Handle(RxVoid.Default);
            if (!string.IsNullOrEmpty(filePath))
            {
                await OpenFileAsync(filePath, allowWorkspaceLoad: openWorkspace);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Open document failed: {Message}", ex.Message);
            SetPersistentStatus(string.Format(UiStrings.DocumentOpenFailed, ex.Message));
        }
    }

    private async System.Threading.Tasks.Task SaveActiveDocumentAsync()
    {
        if (SaveBehavior == SaveBehavior.NoSaving)
        {
            StatusText = UiStrings.SavingDisabled;
            return;
        }

        if (ActiveDocument is not null)
        {
            if (!HasWorkspace && IsUntitledDocument(ActiveDocument))
            {
                bool saved = await TrySaveUntitledDocumentAsync(ActiveDocument);
                if (!saved)
                {
                    return;
                }

                return;
            }

            if (await TrySaveDocumentAsync(ActiveDocument))
            {
                StatusText = string.Format(UiStrings.DocumentSaved, ActiveDocument.FileName);
            }
        }
    }

    private async System.Threading.Tasks.Task SaveAllAsync()
    {
        if (SaveBehavior == SaveBehavior.NoSaving)
        {
            StatusText = UiStrings.SavingDisabled;
            return;
        }

        bool allSaved = true;
        foreach (IEditorDocumentViewModel doc in Documents.ToList())
        {
            if (doc.IsModified)
            {
                if (!HasWorkspace && IsUntitledDocument(doc))
                {
                    bool saved = await TrySaveUntitledDocumentAsync(doc);
                    if (!saved)
                    {
                        continue;
                    }

                    continue;
                }

                allSaved &= await TrySaveDocumentAsync(doc);
            }
        }

        if (allSaved)
        {
            StatusText = UiStrings.AllDocumentsSaved;
        }
    }

    /// <summary>
    /// Saves a document and reports a file that cannot be written, such as a read-only file that source control has
    /// not checked out. The document stays modified, so no change is lost. A file that changed on disk is overwritten
    /// or reloaded as the user decides; a reload returns false, because the editor text was not saved.
    /// </summary>
    // Documents whose save conflict the user left open; automatic saves skip them until a manual save resolves it.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IEditorDocumentViewModel, object> _unresolvedSaveConflicts = new();

    private async System.Threading.Tasks.Task<bool> TrySaveDocumentAsync(IEditorDocumentViewModel doc, bool automatic = false)
    {
        if (automatic && _unresolvedSaveConflicts.TryGetValue(doc, out _))
        {
            return false;
        }

        try
        {
            try
            {
                await doc.SaveCommand.Execute().ToTask();
                _unresolvedSaveConflicts.Remove(doc);
                return true;
            }
            catch (FileChangedOutsideEditorException)
            {
                switch (await SaveConflictInteraction.Handle(doc.FileName))
                {
                    case SaveConflictChoice.Overwrite:
                        await doc.OverwriteFileAsync();
                        _unresolvedSaveConflicts.Remove(doc);
                        return true;
                    case SaveConflictChoice.Reload:
                        await doc.ReloadFromDiskAsync();
                        _unresolvedSaveConflicts.Remove(doc);
                        StatusText = string.Format(UiStrings.DocumentReloadedFromDisk, (object?)doc.FileName);
                        return false;
                    default:
                        _unresolvedSaveConflicts.AddOrUpdate(doc, new object());
                        StatusText = UiStrings.SaveCanceled;
                        return false;
                }
            }
        }
        // InvalidOperationException reports a document that cannot be saved yet, for example during a designer edit;
        // DecoderFallbackException a file on disk that cannot be reloaded as text.
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException
            or DecoderFallbackException)
        {
            _logger.LogWarning("Save document failed: {Message}", ex.Message);
            SetPersistentStatus(FileSystemErrorMessages.TryGetReadOnlyEntryName(ex, doc.FilePath, out string? readOnlyName)
                ? string.Format(UiStrings.DocumentSaveReadOnly, (object?)readOnlyName)
                : string.Format(UiStrings.DocumentSaveFailed, (object?)FileSystemErrorMessages.DescribeReason(ex, doc.FilePath)));
            return false;
        }
    }

    private static bool IsUntitledDocument(IEditorDocumentViewModel doc)
    {
        string tempRoot = System.IO.Path.GetTempPath();
        if (!FileSystemPathComparison.IsSameOrDescendant(doc.FilePath, tempRoot))
        {
            return false;
        }

        string name = System.IO.Path.GetFileNameWithoutExtension(doc.FilePath);
        return name.StartsWith("Untitled-", StringComparison.OrdinalIgnoreCase);
    }

    private async System.Threading.Tasks.Task<bool> TrySaveUntitledDocumentAsync(IEditorDocumentViewModel doc)
    {
        string suggestedName = doc.FileName;
        string? targetPath = await SaveFileInteraction.Handle(suggestedName);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            StatusText = UiStrings.SaveCanceled;
            return false;
        }

        string? text = GetDocumentText(doc);
        if (text is null)
        {
            StatusText = UiStrings.SaveEmptyDocument;
            return false;
        }

        await System.IO.File.WriteAllTextAsync(targetPath, text);

        await EnsureDocumentOpenAsync(targetPath, addRecent: true, updateStatus: false, allowWorkspaceLoad: true);
        CloseDocument(doc);
        StatusText = string.Format(UiStrings.DocumentSaved, (object?)(System.IO.Path.GetFileName(targetPath)));
        return true;
    }

    private static string? GetDocumentText(IEditorDocumentViewModel doc)
    {
        return doc switch
        {
            DesignerDocumentViewModel designer => designer.SyncEngine.CurrentText ?? designer.CodeEditor.Document.Text,
            TextDocumentViewModel text => text.Document.Text,
            _ => null
        };
    }

    private async System.Threading.Tasks.Task RenameSymbolAsync()
    {
        if (ActiveTextDocument is null)
        {
            return;
        }

        LanguageRenameInfo? info = await ActiveTextDocument.PrepareRenameAsync(ActiveTextDocument.CaretOffset);
        if (info is null)
        {
            StatusText = UiStrings.RenameUnavailable;
            return;
        }

        string? newName = await RenameSymbolInteraction.Handle(info);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, info.Name, StringComparison.Ordinal))
        {
            return;
        }

        LanguageWorkspaceEdit? edit = await ActiveTextDocument
            .RenameSymbolAsync(ActiveTextDocument.CaretOffset, newName);
        if (edit is null || edit.DocumentEdits.Count == 0)
        {
            StatusText = UiStrings.RenameNoChanges;
            return;
        }

        await ApplyWorkspaceEditAsync(edit);
        StatusText = string.Format(UiStrings.SymbolRenamed, info.Name, newName);
    }

    private async System.Threading.Tasks.Task FormatDocumentAsync()
    {
        if (ActiveTextDocument is null)
        {
            return;
        }

        IReadOnlyList<TextEdit> edits = await ActiveTextDocument.GetFormattingEditsAsync();
        if (edits.Count == 0)
        {
            StatusText = UiStrings.FormatNoChanges;
            return;
        }

        ActiveTextDocument.ApplyTextEdits(edits);
        StatusText = UiStrings.FormatApplied;
    }

    private async System.Threading.Tasks.Task ShowCodeActionsAsync()
    {
        if (ActiveTextDocument is null)
        {
            return;
        }

        IReadOnlyList<LanguageCodeAction> actions = await ActiveTextDocument
            .GetCodeActionsAsync(ActiveTextDocument.CaretOffset);
        if (actions.Count == 0)
        {
            StatusText = UiStrings.NoCodeActions;
            return;
        }

        LanguageCodeAction? selection = null;
        if (actions.Count == 1)
        {
            selection = actions[0];
        }
        else
        {
            CodeActionPickerRequest request = new(UiStrings.CodeActions, actions);
            selection = await SelectCodeActionInteraction.Handle(request);
        }

        if (selection?.Edit is null)
        {
            return;
        }

        await ApplyWorkspaceEditAsync(selection.Edit);
        StatusText = string.Format(UiStrings.ActionApplied, selection.Title);
    }

    private async System.Threading.Tasks.Task ShowDocumentSymbolsAsync()
    {
        if (ActiveTextDocument is null)
        {
            return;
        }

        IReadOnlyList<LanguageSymbol> symbols = await ActiveTextDocument.GetDocumentSymbolsAsync();
        if (symbols.Count == 0)
        {
            StatusText = UiStrings.NoDocumentSymbols;
            return;
        }

        References.ReplaceItems(symbols.Select(symbol =>
            new ReferenceLocationViewModel(
                symbol.FilePath,
                symbol.Range.Start.Line,
                symbol.Range.Start.Column,
                $"{symbol.Name} ({symbol.Kind})")));

        IsReferencesVisible = true;
        SetDockableVisibility("References", true);
    }

    private async System.Threading.Tasks.Task ShowWorkspaceSymbolsAsync()
    {
        if (ActiveTextDocument is null)
        {
            return;
        }

        string? query = await WorkspaceSymbolQueryInteraction.Handle(string.Empty);
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        IReadOnlyList<LanguageSymbol> symbols = await ActiveTextDocument.GetWorkspaceSymbolsAsync(query);
        if (symbols.Count == 0)
        {
            StatusText = UiStrings.NoWorkspaceSymbols;
            return;
        }

        References.ReplaceItems(symbols.Select(symbol =>
            new ReferenceLocationViewModel(
                symbol.FilePath,
                symbol.Range.Start.Line,
                symbol.Range.Start.Column,
                $"{symbol.Name} ({symbol.Kind})")));

        IsReferencesVisible = true;
        SetDockableVisibility("References", true);
    }

    // Edits go through editor documents: a closed file opens with the change pending, so it is saved and checked
    // out by source control extensions like any other edit instead of being written behind their back.
    internal async System.Threading.Tasks.Task ApplyWorkspaceEditAsync(LanguageWorkspaceEdit edit)
    {
        foreach (LanguageDocumentEdit docEdit in edit.DocumentEdits)
        {
            if (string.IsNullOrWhiteSpace(docEdit.FilePath))
            {
                continue;
            }

            TextDocumentViewModel? textDocument = Documents
                .OfType<TextDocumentViewModel>()
                .FirstOrDefault(doc =>
                    FileSystemPathComparison.Equals(doc.FilePath, docEdit.FilePath));

            if (textDocument is null && System.IO.File.Exists(docEdit.FilePath))
            {
                textDocument = await EnsureDocumentOpenAsync(
                    docEdit.FilePath,
                    addRecent: false,
                    updateStatus: false,
                    allowWorkspaceLoad: false) as TextDocumentViewModel;
            }

            textDocument?.ApplyTextEdits(docEdit.Edits);
        }
    }

    private async System.Threading.Tasks.Task NavigateToLocationAsync(
        LanguageLocation location,
        bool recordHistory = true)
    {
        if (recordHistory)
        {
            RecordNavigation(location);
        }

        IEditorDocumentViewModel? doc = await EnsureDocumentOpenAsync(
            location.FilePath,
            addRecent: false,
            updateStatus: false,
            allowWorkspaceLoad: true);

        if (doc is TextDocumentViewModel textDoc)
        {
            int offset = textDoc.GetOffsetForLineColumn(
                location.Range.Start.Line,
                location.Range.Start.Column);
            textDoc.SetCaretOffset(offset);
            StatusText = string.Format(UiStrings.NavigatedToFile, (object?)(System.IO.Path.GetFileName(location.FilePath)));
        }
    }

    private void RecordNavigation(LanguageLocation target)
    {
        if (!TryGetCurrentLocation(out LanguageLocation current))
        {
            return;
        }

        if (AreLocationsEquivalent(current, target))
        {
            return;
        }

        _backNavigation.Push(current);
        _forwardNavigation.Clear();
        UpdateNavigationState();
    }

    private bool TryGetCurrentLocation(out LanguageLocation location)
    {
        if (ActiveTextDocument is null)
        {
            location = new LanguageLocation
            {
                FilePath = string.Empty,
                Range = new LanguageTextRange(
                    new LanguageTextPosition(1, 1),
                    new LanguageTextPosition(1, 1))
            };
            return false;
        }

        int line = Math.Max(1, ActiveTextDocument.CurrentLine);
        int column = Math.Max(1, ActiveTextDocument.CurrentColumn);

        location = new LanguageLocation
        {
            FilePath = ActiveTextDocument.FilePath,
            Range = new LanguageTextRange(
                new LanguageTextPosition(line, column),
                new LanguageTextPosition(line, column))
        };

        return true;
    }

    private static bool AreLocationsEquivalent(LanguageLocation left, LanguageLocation right)
    {
        return FileSystemPathComparison.Equals(left.FilePath, right.FilePath)
            && left.Range.Start.Line == right.Range.Start.Line
            && left.Range.Start.Column == right.Range.Start.Column;
    }

    private void UpdateNavigationState()
    {
        CanNavigateBack = _backNavigation.Count > 0;
        CanNavigateForward = _forwardNavigation.Count > 0;
    }

    internal async Task NavigateBackFromHistoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await NavigateBackAsync();
    }

    internal async Task NavigateForwardFromHistoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await NavigateForwardAsync();
    }

    private async System.Threading.Tasks.Task NavigateBackAsync()
    {
        if (_backNavigation.Count == 0)
        {
            return;
        }

        if (TryGetCurrentLocation(out LanguageLocation current))
        {
            _forwardNavigation.Push(current);
        }

        LanguageLocation target = _backNavigation.Pop();
        UpdateNavigationState();
        await NavigateToLocationAsync(target, recordHistory: false);
    }

    private async System.Threading.Tasks.Task NavigateForwardAsync()
    {
        if (_forwardNavigation.Count == 0)
        {
            return;
        }

        if (TryGetCurrentLocation(out LanguageLocation current))
        {
            _backNavigation.Push(current);
        }

        LanguageLocation target = _forwardNavigation.Pop();
        UpdateNavigationState();
        await NavigateToLocationAsync(target, recordHistory: false);
    }

    private async System.Threading.Tasks.Task NavigateToOutputMessageAsync(OutputMessage? message)
    {
        if (message is null || string.IsNullOrWhiteSpace(message.FilePath))
        {
            return;
        }

        IEditorDocumentViewModel? doc = await EnsureDocumentOpenAsync(
            message.FilePath,
            addRecent: false,
            updateStatus: false,
            allowWorkspaceLoad: true);

        if (doc is TextDocumentViewModel textDoc)
        {
            LanguageLocation location = new()
            {
                FilePath = message.FilePath,
                Range = new LanguageTextRange(
                    new LanguageTextPosition(message.Line, message.Column),
                    new LanguageTextPosition(message.Line, message.Column))
            };
            RecordNavigation(location);
            int offset = textDoc.GetOffsetForLineColumn(message.Line, message.Column);
            textDoc.SetCaretOffset(offset);
            StatusText = string.Format(UiStrings.NavigatedToFile, (object?)(System.IO.Path.GetFileName(message.FilePath)));
            return;
        }

        if (doc is DesignerDocumentViewModel designer)
        {
            int offset = designer.CodeEditor.GetOffsetForLineColumn(message.Line, message.Column);
            designer.CodeEditor.SetCaretOffset(offset);
            StatusText = string.Format(UiStrings.NavigatedToFile, (object?)(System.IO.Path.GetFileName(message.FilePath)));
        }
    }

    private void AttachAutoSave(DesignerDocumentViewModel doc)
    {
        if (_autoSaveSubscriptions.ContainsKey(doc))
        {
            return;
        }

        IDisposable subscription = doc.SyncEngine.SyncEvents
            .Throttle(TimeSpan.FromMilliseconds(500))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(evt =>
            {
                _ = ReportFailureAsync(AutoSaveAsync(doc));
            });

        _autoSaveSubscriptions[doc] = subscription;
    }

    // A task that nobody awaits reports its failure to the user instead of ending unobserved.
    private async System.Threading.Tasks.Task ReportFailureAsync(System.Threading.Tasks.Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background operation failed: {Message}", ex.Message);
            SetPersistentStatus(string.Format(UiStrings.CommandFailed, (object?)ex.Message));
        }
    }

    private void AttachAutoSave(TextDocumentViewModel doc)
    {
        if (_autoSaveSubscriptions.ContainsKey(doc))
        {
            return;
        }

        IDisposable subscription = Observable.FromEventPattern<EventHandler, EventArgs>(
            h => doc.Document.TextChanged += h,
            h => doc.Document.TextChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(evt =>
            {
                _ = ReportFailureAsync(AutoSaveAsync(doc));
            });

        _autoSaveSubscriptions[doc] = subscription;
    }

    private void DetachAutoSave(IEditorDocumentViewModel doc)
    {
        if (_autoSaveSubscriptions.Remove(doc, out IDisposable? subscription))
        {
            subscription.Dispose();
        }
    }

    private async System.Threading.Tasks.Task AutoSaveAsync(DesignerDocumentViewModel doc)
    {
        if (SaveBehavior != SaveBehavior.AutoSave)
        {
            return;
        }

        if (!doc.IsModified)
        {
            return;
        }

        if (await TrySaveDocumentAsync(doc, automatic: true))
        {
            StatusText = string.Format(UiStrings.DocumentAutoSaved, doc.FileName);
        }
    }

    internal async System.Threading.Tasks.Task AutoSaveAsync(TextDocumentViewModel doc)
    {
        if (SaveBehavior != SaveBehavior.AutoSave)
        {
            return;
        }

        if (!doc.IsModified)
        {
            return;
        }

        if (await TrySaveDocumentAsync(doc, automatic: true))
        {
            StatusText = string.Format(UiStrings.DocumentAutoSaved, doc.FileName);
        }
    }

    public bool TryGetExtensionView(string viewId, out ExtensionViewModel? viewModel)
    {
        return _extensionViews.TryGetValue(viewId, out viewModel);
    }

    // The palette offers the commands that are enabled as menu items are when it opens, and runs the selection only
    // if it is still enabled.
    private async System.Threading.Tasks.Task ShowCommandPaletteAsync()
    {
        List<ExtensionCommandPaletteItemViewModel> items = CommandPaletteItems
            .Where(item => IsExtensionCommandEnabled(item.CommandId))
            .ToList();
        if (items.Count == 0)
        {
            StatusText = UiStrings.NoExtensionCommands;
            return;
        }

        CommandPaletteRequest request = new(UiStrings.CommandPalette, items);
        ExtensionCommandPaletteItemViewModel? selected = await CommandPaletteInteraction.Handle(request);
        if (selected is null)
        {
            return;
        }

        if (!IsExtensionCommandEnabled(selected.CommandId))
        {
            StatusText = string.Format(UiStrings.ExtensionCommandUnavailable, selected.CommandId);
            return;
        }

        await ExecuteExtensionCommandAsync(selected.CommandId);
    }

    private async System.Threading.Tasks.Task ExecuteExtensionCommandAsync(string commandId)
    {
        bool executed = await TryExecuteExtensionCommandAsync(commandId);
        if (executed)
        {
            return;
        }

        StatusText = string.Format(UiStrings.ExtensionCommandUnavailable, commandId);
    }

    private async System.Threading.Tasks.Task<bool> TryExecuteExtensionCommandAsync(string commandId)
    {
        if (_extensionCommands is null)
        {
            return false;
        }

        try
        {
            await _extensionCommands.ExecuteAsync(commandId, null, CancellationToken.None);
            return true;
        }
        catch (InvalidOperationException ex)
            when (ex.Message.StartsWith("Command not found:", StringComparison.Ordinal))
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Extension command failed: {CommandId}", commandId);
            SetPersistentStatus(string.Format(UiStrings.ExtensionCommandFailed, ex.Message));
            return true;
        }
    }

    private void OnExtensionContributionsChanged(object? sender, EventArgs e)
    {
        RxSchedulers.MainThreadScheduler.Schedule(RefreshExtensionContributions);
    }

    private void OnCommandMetadataChanged(object? sender, EventArgs e)
    {
        RxSchedulers.MainThreadScheduler.Schedule(RefreshExtensionContributions);
    }

    private void OnExtensionViewsChanged(object? sender, ExtensionViewRegistryChangedEventArgs e)
    {
        RxSchedulers.MainThreadScheduler.Schedule(RefreshExtensionViews);
    }

    private void RefreshExtensionContributions()
    {
        ConfigurePreviewThemes(ActiveDesignerDocument);
        // Registered preview providers decide which theme the editor options supply.
        if (!_themePreferencesDisposed) ApplyWorkspacePreviewTheme(_workspacePreviewTheme);
        if (_extensionContributions is null)
        {
            DisposeCollectionItems(ExtensionMenuItems);
            AddExtensionManagerMenuItem();
            DisposeCollectionItems(FileMenuItems);
            DisposeCollectionItems(FileNewMenuItems);
            DisposeCollectionItems(FileNewMenuEntries);
            DisposeCollectionItems(ViewMenuItems);
            DisposeCollectionItems(ServerMenuItems);
            DisposeCollectionItems(DebugMenuItems);
            DisposeCollectionItems(EditMenuItems);
            DisposeCollectionItems(EditorContextMenuItems);
            DisposeCollectionItems(WorkspaceMenuItems);
            DisposeCollectionItems(ExtensionToolbarItems);
            DisposeCollectionItems(MainToolbarItems);
            CommandPaletteItems.Clear();
            ClearStatusBarItems();
            ExtensionKeyBindings.Clear();
            _extensionCommandsById.Clear();
            foreach (IDisposable subscription in _extensionCommandSubscriptionsById.Values)
            {
                subscription.Dispose();
            }
            _extensionCommandSubscriptionsById.Clear();
            RefreshExtensionCommandEnablement();
            RefreshExtensionViews();
            return;
        }

        UpdateExtensionMenuItems();
        UpdateExtensionToolbarItems();
        UpdateCommandPaletteItems();
        UpdateExtensionKeyBindings();
        PruneExtensionCommandCache();
        RefreshExtensionCommandEnablement();
        RefreshExtensionViews();
    }

    // Every contribution change of any extension lands here. Custom views whose contribution is unchanged keep
    // their view model, so the extension state survives and no second instance keeps working in the background.
    // Custom view models belong to their extension, which disposes them on deactivation.
    private void RefreshExtensionViews()
    {
        Dictionary<string, ExtensionViewModel> previous = new(_extensionViews, StringComparer.OrdinalIgnoreCase);
        ExtensionViews.Clear();
        _extensionViews.Clear();

        if (_extensionContributions is not null)
        {
            IEnumerable<ExtensionViewContribution> ordered = _extensionContributions.ViewContributions
                .OrderBy(contribution => contribution.Location)
                .ThenBy(contribution => contribution.Priority)
                .ThenBy(contribution => contribution.Title, StringComparer.OrdinalIgnoreCase);

            foreach (ExtensionViewContribution contribution in ordered)
            {
                ExtensionViewModel viewModel;
                if (previous.TryGetValue(contribution.ViewId, out ExtensionViewModel? existing)
                    && existing is ExtensionCustomViewModel { ViewModel: not null }
                    && existing.Contribution == contribution)
                {
                    previous.Remove(contribution.ViewId);
                    viewModel = existing;
                }
                else
                {
                    viewModel = CreateExtensionViewModel(contribution);
                    if (viewModel is ExtensionTreeViewModel treeView)
                    {
                        _ = treeView.LoadAsync(CancellationToken.None);
                    }
                }

                _extensionViews[contribution.ViewId] = viewModel;
                ExtensionViews.Add(viewModel);
            }
        }

        foreach (IDisposable replaced in previous.Values.OfType<IDisposable>())
        {
            replaced.Dispose();
        }

        SyncExtensionDockables();
    }

    private ExtensionViewModel CreateExtensionViewModel(ExtensionViewContribution contribution)
    {
        return contribution.Type switch
        {
            ExtensionViewType.Tree => new ExtensionTreeViewModel(
                contribution,
                ResolveTreeProvider(contribution.ViewId),
                _editorOptions.Current.FileExplorerFoldersBeforeFiles),
            ExtensionViewType.Webview => new ExtensionWebviewViewModel(
                contribution,
                UiStrings.WebviewUnavailable),
            ExtensionViewType.Custom => new ExtensionCustomViewModel(
                contribution,
                ResolveCustomViewModel(contribution.ViewId)),
            _ => new ExtensionWebviewViewModel(
                contribution,
                UiStrings.ViewTypeUnsupported)
        };
    }

    private IExtensionTreeDataProvider ResolveTreeProvider(string viewId)
    {
        if (_extensionViewRegistry is not null
            && _extensionViewRegistry.TryGetTreeProvider(viewId, out IExtensionTreeDataProvider provider))
        {
            return provider;
        }

        return NullExtensionTreeDataProvider.Instance;
    }

    private object? ResolveCustomViewModel(string viewId)
    {
        if (_extensionViewRegistry is not null
            && _extensionViewRegistry.TryGetCustomViewProvider(viewId, out ICustomViewProvider provider))
        {
            return provider.CreateViewModel();
        }

        return null;
    }

    private void AddExtensionManagerMenuItem() => ExtensionMenuItems.Add(new ExtensionMenuItemViewModel(
        "shell.extensions.manage", XamlVisualEditor.Core.Localization.UiStrings.MenuExtensionsManager, ExtensionMenuLocations.Extensions,
        "management", int.MinValue, OpenExtensionManagerCommand));

    private void UpdateExtensionMenuItems()
    {
        DisposeCollectionItems(ExtensionMenuItems);
        DisposeCollectionItems(FileMenuItems);
        DisposeCollectionItems(FileNewMenuItems);
        DisposeCollectionItems(ViewMenuItems);
        DisposeCollectionItems(ServerMenuItems);
        DisposeCollectionItems(DebugMenuItems);
        DisposeCollectionItems(EditMenuItems);
        DisposeCollectionItems(EditorContextMenuItems);
        DisposeCollectionItems(WorkspaceMenuItems);
        AddExtensionManagerMenuItem();

        if (_extensionContributions is null)
        {
            return;
        }

        foreach (ExtensionMenuContribution item in _extensionContributions.MenuItems
            .OrderBy(menu => menu.Location ?? ExtensionMenuLocations.Extensions, StringComparer.OrdinalIgnoreCase)
            .ThenBy(menu => menu.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(menu => menu.Priority)
            .ThenBy(menu => menu.Title, StringComparer.OrdinalIgnoreCase))
        {
            string location = string.IsNullOrWhiteSpace(item.Location)
                ? ExtensionMenuLocations.Extensions
                : item.Location;

            ObservableCollection<ExtensionMenuItemViewModel> target = location switch
            {
                ExtensionMenuLocations.File => FileMenuItems,
                ExtensionMenuLocations.FileNew => FileNewMenuItems,
                ExtensionMenuLocations.Edit => EditMenuItems,
                ExtensionMenuLocations.View => ViewMenuItems,
                ExtensionMenuLocations.ViewServers => ServerMenuItems,
                ExtensionMenuLocations.Debug => DebugMenuItems,
                ExtensionMenuLocations.Tools => ExtensionMenuItems,
                ExtensionMenuLocations.ToolsWorkspace => WorkspaceMenuItems,
                ExtensionMenuLocations.EditorContext => EditorContextMenuItems,
                _ => ExtensionMenuItems
            };

            target.Add(new ExtensionMenuItemViewModel(
                item.CommandId,
                item.Title,
                location,
                item.Group,
                item.Priority,
                CreateExtensionCommand(item.CommandId),
                GetExtensionCommandInputGesture(item.CommandId),
                item.CheckedWhen,
                item.Submenu,
                item.SubmenuGroup));
        }

        ArrangeMenuItems(ExtensionMenuItems, ExtensionMenuGroupOrder);
        ArrangeMenuItems(ViewMenuItems, ViewMenuGroupOrder);
        ArrangeMenuItems(EditMenuItems, []);
        ArrangeMenuItems(EditorContextMenuItems, []);
        RefreshMenuCheckStates();
        UpdateFileNewMenuEntries();
    }

    private static readonly string[] ViewMenuGroupOrder = ["views.debug", "views.bottom", "views.left", "views.right"];

    private static readonly string[] ExtensionMenuGroupOrder = ["management"];

    // Separates the groups of a menu and nests the items that name a submenu under one header per group and
    // submenu title; inside a submenu the submenu groups are separated. Groups listed in the order come first, the
    // others follow alphabetically.
    private static void ArrangeMenuItems(ObservableCollection<ExtensionMenuItemViewModel> items, string[] groupOrder)
    {
        List<ExtensionMenuItemViewModel> arranged = new(items.Count);
        foreach (IGrouping<string, ExtensionMenuItemViewModel> group in OrderGroups(items, item => item.Group, groupOrder))
        {
            AddGroup(arranged, group, NestSubmenus(group));
        }

        items.Clear();
        foreach (ExtensionMenuItemViewModel item in arranged)
        {
            items.Add(item);
        }
    }

    private static List<ExtensionMenuItemViewModel> NestSubmenus(IGrouping<string, ExtensionMenuItemViewModel> group)
    {
        List<ExtensionMenuItemViewModel> entries = new();
        Dictionary<string, (ExtensionMenuItemViewModel Header, List<ExtensionMenuItemViewModel> Children)> submenus =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (ExtensionMenuItemViewModel item in group)
        {
            if (item.Submenu is not { } title)
            {
                entries.Add(item);
                continue;
            }

            if (!submenus.TryGetValue(title, out (ExtensionMenuItemViewModel Header, List<ExtensionMenuItemViewModel> Children) submenu))
            {
                submenu = (new ExtensionMenuItemViewModel(
                    "submenu." + group.Key + "." + title, title, item.Location, item.Group, item.Priority, null), new());
                submenus[title] = submenu;
                entries.Add(submenu.Header);
            }

            submenu.Children.Add(item);
        }

        foreach ((ExtensionMenuItemViewModel header, List<ExtensionMenuItemViewModel> children) in submenus.Values)
        {
            foreach (IGrouping<string, ExtensionMenuItemViewModel> subgroup in OrderGroups(children, item => item.SubmenuGroup, []))
            {
                AddGroup(header.Children, subgroup, subgroup);
            }
        }

        return entries;
    }

    private static IEnumerable<IGrouping<string, ExtensionMenuItemViewModel>> OrderGroups(
        IEnumerable<ExtensionMenuItemViewModel> items,
        Func<ExtensionMenuItemViewModel, string?> groupOf,
        string[] groupOrder)
    {
        return items
            .GroupBy(item => groupOf(item) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => Array.IndexOf(groupOrder, group.Key) is int index and >= 0 ? index : groupOrder.Length)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
    }

    private static void AddGroup(
        ICollection<ExtensionMenuItemViewModel> target,
        IGrouping<string, ExtensionMenuItemViewModel> group,
        IEnumerable<ExtensionMenuItemViewModel> entries)
    {
        if (target.Count > 0)
        {
            ExtensionMenuItemViewModel first = group.First();
            target.Add(ExtensionMenuItemViewModel.CreateSeparator(first.Location, group.Key));
        }

        foreach (ExtensionMenuItemViewModel entry in entries)
        {
            target.Add(entry);
        }
    }

    private IEnumerable<ObservableCollection<ExtensionMenuItemViewModel>> GetExtensionMenus()
    {
        return
        [
            ExtensionMenuItems, FileMenuItems, FileNewMenuItems, ViewMenuItems, ServerMenuItems, DebugMenuItems,
            EditMenuItems, EditorContextMenuItems, WorkspaceMenuItems
        ];
    }

    // Toggle items read their check mark from the context keys, so a changed key updates the menus in place.
    private void RefreshMenuCheckStates()
    {
        foreach (ObservableCollection<ExtensionMenuItemViewModel> menu in GetExtensionMenus())
        {
            RefreshMenuCheckStates(menu);
        }
    }

    private void RefreshMenuCheckStates(IEnumerable<ExtensionMenuItemViewModel> items)
    {
        foreach (ExtensionMenuItemViewModel item in items)
        {
            if (item.CheckedWhen is { } checkedWhen)
            {
                item.IsChecked = EvaluateWhenExpressionForContext(checkedWhen);
            }

            RefreshMenuCheckStates(item.Children);
        }
    }

    private void UpdateFileNewMenuEntries()
    {
        DisposeCollectionItems(FileNewMenuEntries);

        if (NewDocumentCommand is null)
        {
            return;
        }

        FileNewMenuEntries.Add(new ExtensionMenuItemViewModel(
            "builtin.newFile",
            XamlVisualEditor.Core.Localization.UiStrings.MenuFile,
            ExtensionMenuLocations.FileNew,
            "builtin",
            -100,
            NewDocumentCommand,
            "Ctrl+N"));

        foreach (ExtensionMenuItemViewModel item in FileNewMenuItems)
        {
            FileNewMenuEntries.Add(new ExtensionMenuItemViewModel(
                item.CommandId,
                item.Title,
                item.Location,
                item.Group,
                item.Priority,
                item.Command ?? NewDocumentCommand,
                item.InputGestureText));
        }
    }

    private void UpdateExtensionToolbarItems()
    {
        DisposeCollectionItems(ExtensionToolbarItems);
        DisposeCollectionItems(MainToolbarItems);

        if (_extensionContributions is null)
        {
            return;
        }

        foreach (ExtensionToolbarContribution item in _extensionContributions.ToolbarItems
            .OrderBy(toolbar => toolbar.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(toolbar => toolbar.Priority)
            .ThenBy(toolbar => toolbar.Title, StringComparer.OrdinalIgnoreCase))
        {
            string location = string.IsNullOrWhiteSpace(item.Location)
                ? ExtensionToolbarLocations.Extensions
                : item.Location;

            ObservableCollection<ExtensionToolbarItemViewModel> target =
                string.Equals(location, ExtensionToolbarLocations.Main, StringComparison.OrdinalIgnoreCase)
                    ? MainToolbarItems
                    : ExtensionToolbarItems;

            target.Add(new ExtensionToolbarItemViewModel(
                item.CommandId,
                item.Title,
                item.Tooltip,
                location,
                item.Group,
                item.Priority,
                CreateExtensionCommand(item.CommandId),
                item.IconPathData));
        }
    }

    private void UpdateCommandPaletteItems()
    {
        CommandPaletteItems.Clear();

        if (_extensionContributions is null)
        {
            return;
        }

        foreach (ExtensionCommandPaletteContribution item in _extensionContributions.CommandPaletteItems
            .OrderBy(palette => palette.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(palette => palette.Title, StringComparer.OrdinalIgnoreCase))
        {
            CommandPaletteItems.Add(new ExtensionCommandPaletteItemViewModel(
                item.CommandId,
                item.Title,
                item.Category));
        }
    }

    private void UpdateExtensionKeyBindings()
    {
        ExtensionKeyBindings.Clear();

        if (_commandMetadata is null)
        {
            return;
        }

        HashSet<string> registeredGestures = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, CommandMetadata> entry in _commandMetadata.GetAll()
            .OrderBy(item => item.Value.Priority)
            .ThenBy(item => item.Value.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            string? gesture = GetCommandInputGesture(entry.Key);
            if (string.IsNullOrWhiteSpace(gesture))
            {
                continue;
            }

            if (!registeredGestures.Add(gesture))
            {
                continue;
            }

            ExtensionKeyBindings.Add(new ExtensionKeyBindingViewModel(
                entry.Key,
                gesture,
                CreateExtensionCommand(entry.Key),
                entry.Value.KeybindingScope));
        }
    }

    public void UpsertStatusBarItem(
        string itemId,
        string text,
        string? tooltip,
        string? commandId,
        StatusBarAlignment alignment,
        int priority,
        bool isVisible)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        ObservableCollection<ExtensionStatusBarItemViewModel> target =
            alignment == StatusBarAlignment.Left ? LeftStatusBarItems : RightStatusBarItems;
        ObservableCollection<ExtensionStatusBarItemViewModel> other =
            alignment == StatusBarAlignment.Left ? RightStatusBarItems : LeftStatusBarItems;

        if (!_statusBarItemsById.TryGetValue(itemId, out ExtensionStatusBarItemViewModel? item))
        {
            item = new ExtensionStatusBarItemViewModel(
                itemId,
                text,
                tooltip,
                commandId,
                alignment,
                priority,
                string.IsNullOrWhiteSpace(commandId) ? null : CreateExtensionCommand(commandId));
            _statusBarItemsById[itemId] = item;
        }
        else
        {
            item.Text = text;
            item.Tooltip = tooltip;
            item.CommandId = commandId;
            item.Alignment = alignment;
            item.Priority = priority;
            item.SetCommand(string.IsNullOrWhiteSpace(commandId) ? null : CreateExtensionCommand(commandId));
        }

        if (!isVisible)
        {
            target.Remove(item);
            other.Remove(item);
            return;
        }

        other.Remove(item);
        if (!target.Contains(item))
        {
            target.Add(item);
        }

        SortStatusBarItems(target);
    }

    public void RemoveStatusBarItem(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        if (!_statusBarItemsById.Remove(itemId, out ExtensionStatusBarItemViewModel? item))
        {
            return;
        }

        LeftStatusBarItems.Remove(item);
        RightStatusBarItems.Remove(item);
        item.Dispose();
    }

    private static void SortStatusBarItems(ObservableCollection<ExtensionStatusBarItemViewModel> items)
    {
        List<ExtensionStatusBarItemViewModel> sorted = items
            .OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int index = 0; index < sorted.Count; index++)
        {
            ExtensionStatusBarItemViewModel expected = sorted[index];
            if (!ReferenceEquals(items[index], expected))
            {
                int currentIndex = items.IndexOf(expected);
                if (currentIndex >= 0)
                {
                    items.Move(currentIndex, index);
                }
            }
        }
    }

    private ICommand CreateExtensionCommand(string commandId)
    {
        if (_extensionCommandsById.TryGetValue(commandId, out ICommand? existing))
        {
            return existing;
        }

        ExtensionAsyncCommand command = new(
            () => ExecuteExtensionCommandAsync(commandId),
            () => IsExtensionCommandEnabled(commandId));
        _extensionCommandsById[commandId] = command;
        EnsureExtensionCommandSubscription(commandId, command);
        return command;
    }

    private void RefreshExtensionCommandEnablement()
    {
        AttachExtensionCommandSubscriptions();

        foreach (ICommand command in _extensionCommandsById.Values)
        {
            if (command is ExtensionAsyncCommand extensionCommand)
            {
                extensionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void OnContextKeysChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnContextKeysChanged(sender, e));
            return;
        }

        RefreshMenuCheckStates();
        RefreshExtensionCommandEnablement();
    }

    private static void AddMenuCommandIds(IEnumerable<ExtensionMenuItemViewModel> items, HashSet<string> commandIds)
    {
        foreach (ExtensionMenuItemViewModel item in items.Where(item => item.Command is not null))
        {
            commandIds.Add(item.CommandId);
        }

        foreach (ExtensionMenuItemViewModel submenu in items.Where(item => item.Children.Count > 0))
        {
            AddMenuCommandIds(submenu.Children, commandIds);
        }
    }

    private void PruneExtensionCommandCache()
    {
        HashSet<string> activeCommandIds = new(StringComparer.Ordinal);

        // Commands inside submenus stay cached, so context key changes keep updating their enablement.
        foreach (ObservableCollection<ExtensionMenuItemViewModel> menu in GetExtensionMenus())
        {
            AddMenuCommandIds(menu, activeCommandIds);
        }

        foreach (ExtensionToolbarItemViewModel item in MainToolbarItems)
        {
            activeCommandIds.Add(item.CommandId);
        }

        foreach (ExtensionToolbarItemViewModel item in ExtensionToolbarItems)
        {
            activeCommandIds.Add(item.CommandId);
        }

        foreach (ExtensionCommandPaletteItemViewModel item in CommandPaletteItems)
        {
            activeCommandIds.Add(item.CommandId);
        }

        foreach (ExtensionKeyBindingViewModel item in ExtensionKeyBindings)
        {
            activeCommandIds.Add(item.CommandId);
        }

        foreach (ExtensionStatusBarItemViewModel item in LeftStatusBarItems)
        {
            if (!string.IsNullOrWhiteSpace(item.CommandId))
            {
                activeCommandIds.Add(item.CommandId);
            }
        }

        foreach (ExtensionStatusBarItemViewModel item in RightStatusBarItems)
        {
            if (!string.IsNullOrWhiteSpace(item.CommandId))
            {
                activeCommandIds.Add(item.CommandId);
            }
        }

        List<string> staleKeys = _extensionCommandsById.Keys
            .Where(key => !activeCommandIds.Contains(key))
            .ToList();

        foreach (string key in staleKeys)
        {
            _extensionCommandsById.Remove(key);
            if (_extensionCommandSubscriptionsById.TryGetValue(key, out IDisposable? subscription))
            {
                subscription.Dispose();
                _extensionCommandSubscriptionsById.Remove(key);
            }
        }
    }

    private bool IsExtensionCommandEnabled(string commandId)
    {
        if (_commandMetadata is not null
            && _commandMetadata.TryGet(commandId, out CommandMetadata metadata)
            && !EvaluateWhenExpressionForContext(metadata.When))
        {
            return false;
        }

        if (!TryGetHostCommand(commandId, out ICommand? hostCommand))
        {
            return true;
        }

        if (hostCommand is null)
        {
            return true;
        }

        return hostCommand.CanExecute(null);
    }

    private void AttachExtensionCommandSubscriptions()
    {
        foreach ((string commandId, ICommand command) in _extensionCommandsById)
        {
            if (command is ExtensionAsyncCommand extensionCommand)
            {
                EnsureExtensionCommandSubscription(commandId, extensionCommand);
            }
        }
    }

    private void EnsureExtensionCommandSubscription(string commandId, ExtensionAsyncCommand extensionCommand)
    {
        if (_extensionCommandSubscriptionsById.ContainsKey(commandId))
        {
            return;
        }

        if (!TryGetHostCommand(commandId, out ICommand? hostCommand))
        {
            return;
        }

        if (hostCommand is null)
        {
            return;
        }

        EventHandler handler = (_, _) => extensionCommand.NotifyCanExecuteChanged();
        hostCommand.CanExecuteChanged += handler;
        _extensionCommandSubscriptionsById[commandId] =
            Disposable.Create(() => hostCommand.CanExecuteChanged -= handler);
    }

    private bool TryGetHostCommand(string commandId, out ICommand? command)
    {
        command = commandId switch
        {
            "editing.undo" => UndoCommand,
            "editing.redo" => RedoCommand,
            "editing.cut" => CutCommand,
            "editing.copy" => CopyCommand,
            "editing.paste" => PasteCommand,
            "editing.duplicate" => DuplicateCommand,
            "editing.delete" => DeleteCommand,
            "editing.selectAll" => SelectAllCommand,
            "navigation.renameSymbol" => RenameSymbolCommand,
            "navigation.formatDocument" => FormatDocumentCommand,
            "navigation.codeActions" => CodeActionsCommand,
            "navigation.documentSymbols" => DocumentSymbolsCommand,
            "navigation.workspaceSymbols" => WorkspaceSymbolsCommand,
            "debug.breakpoints.toggleView" => ToggleBreakpointsCommand,
            "debug.callStack.toggleView" => ToggleCallStackCommand,
            "debug.locals.toggleView" => ToggleLocalsCommand,
            "debug.watches.toggleView" => ToggleWatchesCommand,
            "debug.start" => StartDebugCommand,
            "debug.stop" => StopDebugCommand,
            "debug.continue" => ContinueDebugCommand,
            "debug.stepOver" => StepOverCommand,
            "debug.stepIn" => StepInCommand,
            "debug.stepOut" => StepOutCommand,
            "debug.pause" => PauseDebugCommand,
            "debug.toggleBreakpoint" => ToggleBreakpointCommand,
            "run.start" => StartRunCommand,
            "run.stop" => StopRunCommand,
            "terminal.new" => NewTerminalCommand,
            _ => null
        };

        return command is not null;
    }

    private bool EvaluateWhenExpressionForContext(string? when)
    {
        if (string.IsNullOrWhiteSpace(when))
        {
            return true;
        }

        foreach (string orTerm in when.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            bool andResult = true;
            foreach (string rawCondition in orTerm.Split("&&", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                bool negate = rawCondition.StartsWith("!", StringComparison.Ordinal);
                string condition = negate ? rawCondition[1..].Trim() : rawCondition.Trim();
                condition = condition.Trim('(', ')');

                if (!TryGetWhenContextValue(condition, out bool value))
                {
                    andResult = false;
                    break;
                }

                bool evaluated = negate ? !value : value;
                if (!evaluated)
                {
                    andResult = false;
                    break;
                }
            }

            if (andResult)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetWhenContextValue(string condition, out bool value)
    {
        switch (condition.Trim().ToLowerInvariant())
        {
            case "true":
                value = true;
                return true;
            case "false":
                value = false;
                return true;
            case "hasactivedocument":
            case "editor.hasactivedocument":
                value = ActiveDocument is not null;
                return true;
            case "hasdesignerdocument":
            case "editor.hasdesignerdocument":
                value = ActiveDesignerDocument is not null;
                return true;
            case "hastextdocument":
            case "editor.hastextdocument":
                value = ActiveTextDocument is not null;
                return true;
            case "hasworkspace":
            case "workspace.loaded":
                value = HasWorkspace;
                return true;
            case "workspace.loading":
                value = IsWorkspaceLoading;
                return true;
            case "workspace.commandrunning":
                value = IsWorkspaceCommandRunning;
                return true;
            case "workspace.busy":
                value = IsWorkspaceLoading || IsWorkspaceCommandRunning;
                return true;
            case "cannavigateback":
            case "navigation.cannavigateback":
                value = CanNavigateBack;
                return true;
            case "cannavigateforward":
            case "navigation.cannavigateforward":
                value = CanNavigateForward;
                return true;
            case "isrunactive":
            case "run.active":
                value = IsRunActive;
                return true;
            case "debug.active":
            case "isdebugactive":
                value = Debugger.State is DebugSessionState.Initializing
                    or DebugSessionState.Running
                    or DebugSessionState.Paused;
                return true;
            case "debug.running":
            case "isdebugrunning":
                value = Debugger.State == DebugSessionState.Running;
                return true;
            case "debug.paused":
            case "isdebugpaused":
                value = Debugger.State == DebugSessionState.Paused;
                return true;
            case "debug.idle":
            case "isdebugidle":
                value = IsDebugIdle(Debugger.State);
                return true;
            default:
                value = false;
                return _contextKeys?.TryGet(condition, out value) == true;
        }
    }

    private string? GetExtensionCommandInputGesture(string commandId)
    {
        return GetCommandInputGesture(commandId);
    }

    private string? GetCommandInputGesture(string commandId)
    {
        if (_commandMetadata is null || !_commandMetadata.TryGet(commandId, out CommandMetadata metadata))
        {
            return null;
        }

        string? gesture = OperatingSystem.IsMacOS()
            ? (metadata.MacKeybinding ?? metadata.Keybinding)
            : (metadata.Keybinding ?? metadata.MacKeybinding);

        return string.IsNullOrWhiteSpace(gesture) ? null : gesture.Trim();
    }

    private static void DisposeCollectionItems<T>(ObservableCollection<T> items)
    {
        foreach (IDisposable disposable in items.OfType<IDisposable>().ToList())
        {
            if (disposable is ExtensionMenuItemViewModel { Children.Count: > 0 } submenu)
            {
                DisposeCollectionItems(submenu.Children);
            }

            disposable.Dispose();
        }

        items.Clear();
    }

    private void ClearStatusBarItems()
    {
        foreach (ExtensionStatusBarItemViewModel item in LeftStatusBarItems
            .Concat(RightStatusBarItems)
            .Concat(_statusBarItemsById.Values)
            .Distinct()
            .ToList())
        {
            item.Dispose();
        }

        LeftStatusBarItems.Clear();
        RightStatusBarItems.Clear();
        _statusBarItemsById.Clear();
    }

    // A panel joins a layout on its own only when the preset of a new layout lists it, and only once: a panel
    // that a loaded layout does not contain, or that the user closed, stays closed until the view menu opens it.
    private void BeginExtensionViewPlacement(IReadOnlySet<string>? presetPanels)
    {
        _presetPanels = presetPanels;
        _placedExtensionViews.Clear();
    }

    private void SyncExtensionDockables()
    {
        if (DockLayout is null)
        {
            return;
        }

        DisableShellOutputIfExtensionPresent();
        _extensionTools.Clear();

        foreach (ExtensionTool tool in XamlEditorDockFactory.FindDockables<ExtensionTool>(DockLayout))
        {
            if (string.IsNullOrWhiteSpace(tool.ViewId))
            {
                if (tool.Owner is IDock dock && dock.VisibleDockables is not null)
                {
                    dock.VisibleDockables.Remove(tool);
                }
            }
            else if (_extensionViews.TryGetValue(tool.ViewId, out ExtensionViewModel? viewModel))
            {
                tool.ExtensionViewModel = viewModel;
                tool.Title = viewModel.Title;
                _extensionTools[tool.ViewId] = tool;
                _placedExtensionViews.Add(tool.ViewId);
            }
        }

        foreach (ExtensionViewModel view in ExtensionViews)
        {
            if (_extensionTools.ContainsKey(view.ViewId))
            {
                continue;
            }

            string toolId = ExtensionTool.BuildId(view.ViewId);
            ExtensionTool? existing = XamlEditorDockFactory.FindDockable<ExtensionTool>(DockLayout, toolId);
            if (existing is not null)
            {
                existing.ExtensionViewModel = view;
                existing.Title = view.Title;
                _extensionTools[view.ViewId] = existing;
                _placedExtensionViews.Add(view.ViewId);
                continue;
            }

            if (_presetPanels?.Contains(view.ViewId) != true || !_placedExtensionViews.Add(view.ViewId))
            {
                continue;
            }

            ExtensionTool? created = DockFactory.AddExtensionTool(DockLayout, view);
            if (created is not null)
            {
                _extensionTools[view.ViewId] = created;
            }
        }
    }

    private void DisableShellOutputIfExtensionPresent()
    {
        const string outputViewId = "output.panel";
        if (!_extensionViews.ContainsKey(outputViewId))
        {
            return;
        }

        OutputTool? outputTool = XamlEditorDockFactory.FindDockable<OutputTool>(DockLayout, "Output");
        if (outputTool is null)
        {
            return;
        }

        DockFactory.CloseDockable(outputTool);

        IsOutputVisible = false;
    }

    private void ResetLayout()
    {
        InstallPresetLayout(FindLayoutPreset(LayoutPresetCatalog.DefaultId)!);
        SetCurrentLayout(null, isLoaded: false);

        StatusText = UiStrings.LayoutReset;
    }

    /// <summary>
    /// Tracks which layout is active. <paramref name="isLoaded"/> is false for the default layout
    /// (no status bar entry); a null <paramref name="name"/> with a loaded layout shows "unnamed".
    /// </summary>
    private void SetCurrentLayout(string? name, bool isLoaded)
    {
        CurrentLayoutName = name;
        LayoutStatusText = isLoaded
            ? string.Format(UiStrings.StatusLayoutFormat, name ?? UiStrings.LayoutUnnamed)
            : null;
    }

    /// <summary>
    /// Replaces the active dock layout and re-attaches the open documents and running terminals, which a
    /// serialized layout does not contain. A loaded layout shows the panels it contains; a layout built from a
    /// preset receives the extension panels that <paramref name="presetPanels"/> lists.
    /// </summary>
    private void InstallLayout(IRootDock layout, IReadOnlySet<string>? presetPanels)
    {
        XamlEditorDockFactory.EnsureLayoutDefaults(layout);
        DockFactory.InitLayout(layout);
        DockFactory.EnsureOwnerReferences(layout);
        DockFactory.ConfigureToolViewModels(layout);
        DockFactory.ConfigureDocumentViewModels(layout);
        DockLayout = layout;
        BeginExtensionViewPlacement(presetPanels);
        _extensionTools.Clear();
        SyncExtensionDockables();
        ReattachRuntimeDockables();
    }

    private void ReattachRuntimeDockables()
    {
        _dockDocuments.Clear();
        foreach (IEditorDocumentViewModel document in Documents)
        {
            AddDocumentToDock(document);
        }

        if (ActiveDocument is IEditorDocumentViewModel active)
        {
            SetActiveDockDocument(active);
        }

        _terminalTools.Clear();
        foreach (TerminalViewModel terminal in Terminals)
        {
            AttachTerminalTool(terminal);
        }
    }

    /// <summary>
    /// Applies one of the ready-made layouts: builds a new layout that contains only the panels of the preset.
    /// </summary>
    public void ApplyLayoutPreset(string presetId)
    {
        LayoutPreset? preset = FindLayoutPreset(presetId);
        if (preset is null)
        {
            return;
        }

        InstallPresetLayout(preset);
        SetCurrentLayout(null, isLoaded: true);
        StatusText = string.Format(UiStrings.LayoutLoaded, preset.Title);
    }

    private LayoutPreset? FindLayoutPreset(string presetId)
    {
        foreach (LayoutPreset candidate in _layoutPresets)
        {
            if (string.Equals(candidate.Id, presetId, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private void InstallPresetLayout(LayoutPreset preset)
    {
        IRootDock layout = DockFactory.CreateDefaultLayout(preset.IsVisible);
        InstallLayout(layout, preset.VisiblePanelIds);
        ArrangePresetLayout(layout, preset);
        DockFactory.SaveLayout(layout);
    }

    private void ArrangePresetLayout(IRootDock layout, LayoutPreset preset)
    {
        if (preset.ExpandPinnedPanels)
        {
            foreach (IDockable tool in XamlEditorDockFactory.FindDockables<Tool>(layout))
            {
                if (tool is not TerminalTool && DockFactory.IsDockablePinned(tool, layout))
                {
                    DockFactory.UnpinDockable(tool);
                }
            }
        }

        EnsureActiveToolDockables(layout);
        if (preset.CollapseBottomDock)
        {
            DockFactory.CollapseBottomToolDock(layout);
        }

        IsBreakpointsVisible = preset.IsVisible("Breakpoints");
        IsCallStackVisible = preset.IsVisible("CallStack");
        IsLocalsVisible = preset.IsVisible("Locals");
        IsWatchesVisible = preset.IsVisible("Watches");
        IsOutputVisible = preset.IsVisible("Output") || preset.IsVisible("output.panel");
    }

    private static void EnsureActiveToolDockables(IRootDock layout)
    {
        foreach (ToolDock dock in XamlEditorDockFactory.FindDockables<ToolDock>(layout))
        {
            if (dock.VisibleDockables is null || dock.VisibleDockables.Count == 0)
            {
                dock.ActiveDockable = null;
                continue;
            }

            if (dock.ActiveDockable is null || !dock.VisibleDockables.Contains(dock.ActiveDockable))
            {
                dock.ActiveDockable = dock.VisibleDockables[0];
            }
        }
    }

    /// <summary>
    /// Loads a layout that the user saved earlier under the given name.
    /// </summary>
    public void LoadUserLayout(string name)
    {
        if (!_userLayoutStore.Exists(name))
        {
            RefreshSavedLayoutMenuItems();
            StatusText = string.Format(UiStrings.LayoutNotFound, name);
            return;
        }

        IRootDock? layout = DockFactory.LoadLayout(_userLayoutStore.GetPath(name));
        if (layout is null)
        {
            SetPersistentStatus(string.Format(UiStrings.LayoutLoadFailed, name));
            return;
        }

        InstallLayout(layout, presetPanels: null);
        SyncPanelVisibilityFlagsFromLayout();
        DockFactory.SaveLayout(layout, layoutName: name);
        SetCurrentLayout(name, isLoaded: true);
        StatusText = string.Format(UiStrings.LayoutLoaded, name);
    }

    private void SyncPanelVisibilityFlagsFromLayout()
    {
        IsBreakpointsVisible = IsToolVisible("Breakpoints");
        IsCallStackVisible = IsToolVisible("CallStack");
        IsLocalsVisible = IsToolVisible("Locals");
        IsWatchesVisible = IsToolVisible("Watches");
        IsOutputVisible = IsToolVisible("Output") || IsExtensionViewVisible("output.panel");
    }

    // The view toggles decide from the layout, not from the visibility flags, which a restored layout does not update.
    private bool IsDockableShown(string id)
    {
        IDockable? dockable = DockLayout is null ? null : XamlEditorDockFactory.FindDockable<IDockable>(DockLayout, id);
        if (dockable is not null && DockFactory.IsDockablePinned(dockable, DockLayout))
        {
            return DockLayout!.PinnedDock?.VisibleDockables?.Contains(dockable) == true;
        }

        return IsToolVisible(id);
    }

    private bool IsToolVisible(string id)
    {
        IDockable? dockable = XamlEditorDockFactory.FindDockable<IDockable>(DockLayout, id);
        return dockable?.Owner is IDock dock
            && dock.VisibleDockables is not null
            && dock.VisibleDockables.Contains(dockable);
    }

    private async Task SaveUserLayoutAsync(bool promptForName)
    {
        string? name = CurrentLayoutName;
        if (promptForName || string.IsNullOrWhiteSpace(name))
        {
            name = await LayoutNameInteraction.Handle(name ?? string.Empty);
            if (name is null)
            {
                return;
            }

            name = name.Trim();
        }

        if (!_userLayoutStore.IsValidName(name))
        {
            StatusText = UiStrings.LayoutInvalidName;
            return;
        }

        if (!DockFactory.SaveLayout(DockLayout, _userLayoutStore.GetPath(name)))
        {
            SetPersistentStatus(string.Format(UiStrings.LayoutSaveFailed, name));
            return;
        }

        SetCurrentLayout(name, isLoaded: true);
        RefreshSavedLayoutMenuItems();
        StatusText = string.Format(UiStrings.LayoutSaved, name);
    }

    /// <summary>
    /// Deletes the saved file of the active user layout after the user confirmed. The dock keeps
    /// its current arrangement, which becomes an unnamed layout.
    /// </summary>
    private async Task DeleteCurrentLayoutAsync()
    {
        string? name = CurrentLayoutName;
        if (name is null)
        {
            return;
        }

        bool confirmed = await ConfirmLayoutDeleteInteraction.Handle(name);
        if (!confirmed || !string.Equals(CurrentLayoutName, name, StringComparison.Ordinal))
        {
            return;
        }

        bool deleted = _userLayoutStore.Delete(name);
        RefreshSavedLayoutMenuItems();
        SetCurrentLayout(null, isLoaded: true);
        StatusText = deleted
            ? string.Format(UiStrings.LayoutDeleted, name)
            : string.Format(UiStrings.LayoutNotFound, name);
    }

    private void OnUserLayoutRenamed(string oldName, string newName)
    {
        if (string.Equals(CurrentLayoutName, oldName, StringComparison.Ordinal))
        {
            SetCurrentLayout(newName, isLoaded: true);
        }

        RefreshSavedLayoutMenuItems();
        StatusText = string.Format(UiStrings.LayoutRenamed, oldName, newName);
    }

    // The dock keeps the arrangement of a deleted active layout, which becomes an unnamed layout.
    private void OnUserLayoutDeleted(string name)
    {
        if (string.Equals(CurrentLayoutName, name, StringComparison.Ordinal))
        {
            SetCurrentLayout(null, isLoaded: true);
        }

        RefreshSavedLayoutMenuItems();
        StatusText = string.Format(UiStrings.LayoutDeleted, name);
    }

    private void InitializeLayoutPresetMenuItems()
    {
        DisposeCollectionItems(LayoutPresetMenuItems);
        int priority = 0;
        foreach (LayoutPreset preset in _layoutPresets)
        {
            string presetId = preset.Id;
            LayoutPresetMenuItems.Add(new ExtensionMenuItemViewModel(
                "layout.preset." + presetId,
                preset.Title,
                ExtensionMenuLocations.View,
                "layout.presets",
                priority += 10,
                ReactiveCommand.Create(() => ApplyLayoutPreset(presetId))));
        }
    }

    private void RefreshSavedLayoutMenuItems()
    {
        DisposeCollectionItems(SavedLayoutMenuItems);
        int priority = 0;
        foreach (string name in _userLayoutStore.ListNames())
        {
            string layoutName = name;
            SavedLayoutMenuItems.Add(new ExtensionMenuItemViewModel(
                "layout.user." + layoutName,
                layoutName,
                ExtensionMenuLocations.View,
                "layout.saved",
                priority += 10,
                ReactiveCommand.Create(() => LoadUserLayout(layoutName))));
        }
    }

    private sealed partial class NullExtensionManager : IExtensionManager
    {
        public Task<IReadOnlyList<ExtensionPackageInfo>> GetInstalledAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<ExtensionPackageInfo>>(Array.Empty<ExtensionPackageInfo>());
        }

        public Task<ExtensionPackageInfo> InstallAsync(string packagePath, CancellationToken ct)
        {
            throw new InvalidOperationException(UiStrings.ExtensionManagerUnavailable);
        }

        public Task UninstallAsync(string extensionId, CancellationToken ct)
        {
            throw new InvalidOperationException(UiStrings.ExtensionManagerUnavailable);
        }

        public Task<bool> GetEnabledAsync(string extensionId, CancellationToken ct)
        {
            return Task.FromResult(false);
        }

        public Task SetEnabledAsync(string extensionId, bool enabled, CancellationToken ct)
        {
            throw new InvalidOperationException(UiStrings.ExtensionManagerUnavailable);
        }

        public Task<IReadOnlyList<ExtensionUpdateInfo>> CheckForUpdatesAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<ExtensionUpdateInfo>>(Array.Empty<ExtensionUpdateInfo>());
        }
    }

    public TerminalViewModel CreateTerminalSession(TerminalSessionOptions options)
    {
        if (_terminalService is null)
        {
            throw new InvalidOperationException(UiStrings.TerminalServiceUnavailable);
        }

        TerminalSessionOptions resolved = new()
        {
            Columns = options.Columns <= 0 ? 120 : options.Columns,
            Rows = options.Rows <= 0 ? 40 : options.Rows,
            ScrollbackLimit = options.ScrollbackLimit <= 0 ? 50000 : options.ScrollbackLimit,
            WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? GetTerminalWorkingDirectory()
                : options.WorkingDirectory,
            Command = options.Command,
            Arguments = options.Arguments ?? Array.Empty<string>(),
            Environment = options.Environment is null || options.Environment.Count == 0
                ? new Dictionary<string, string>
                {
                    ["TERM"] = "xterm-256color",
                    ["COLORTERM"] = "truecolor"
                }
                : new Dictionary<string, string>(options.Environment)
        };

        string? logPath = Environment.GetEnvironmentVariable("XVE_TERMINAL_LOG");
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            resolved.EnableSequenceLog = true;
            resolved.SequenceLogPath = logPath;
        }

        ITerminalSession session = _terminalService.CreateSession(resolved);
        TerminalViewModel terminalVm = new(session, resolved.Command);
        try { terminalVm.Start(); }
        catch
        {
            terminalVm.Dispose();
            throw;
        }
        Terminals.Add(terminalVm);

        if (DockLayout is not null && AttachTerminalTool(terminalVm) is not null)
        {
            Guid terminalId = terminalVm.Id;
            // Exited arrives on the reader thread of the PTY session.
            terminalVm.Exited += _ => Dispatcher.UIThread.Post(() => CloseTerminalSession(terminalId));
        }

        StatusText = XamlVisualEditor.Core.Localization.UiStrings.TerminalInternalStarted;
        return terminalVm;
    }

    private TerminalTool? AttachTerminalTool(TerminalViewModel terminalVm)
    {
        TerminalTool? tool = DockFactory.AddTerminalTool(DockLayout, terminalVm);
        if (tool is null)
        {
            return null;
        }

        _terminalTools[terminalVm.Id] = tool;
        if (_terminalTitleSubscriptions.TryGetValue(terminalVm.Id, out IDisposable? previousSubscription))
        {
            previousSubscription.Dispose();
        }

        _terminalTitleSubscriptions[terminalVm.Id] = terminalVm.WhenAnyValue(x => x.Title)
            .Subscribe(title => tool.Title = title);
        return tool;
    }

    public Guid? GetActiveTerminalId()
    {
        if (DockLayout?.ActiveDockable is TerminalTool terminalTool
            && terminalTool.TerminalViewModel is TerminalViewModel terminalVm)
        {
            return terminalVm.Id;
        }

        return null;
    }

    public bool CloseTerminalSession(Guid terminalId)
    {
        if (_terminalTools.TryGetValue(terminalId, out TerminalTool? terminalTool))
        {
            if (terminalTool.Owner is IDock)
            {
                DockFactory.CloseDockable(terminalTool);
                return true;
            }

            if (terminalTool.TerminalViewModel is TerminalViewModel orphanedVm)
            {
                RemoveTerminalSession(orphanedVm);
                terminalTool.TerminalViewModel = null;
                _terminalTools.Remove(terminalId);
                return true;
            }
        }

        TerminalViewModel? terminal = Terminals.FirstOrDefault(vm => vm.Id == terminalId);
        if (terminal is null)
        {
            return false;
        }

        RemoveTerminalSession(terminal);
        return true;
    }

    private string GetTerminalWorkingDirectory() => string.IsNullOrWhiteSpace(_workspacePath)
        ? Environment.CurrentDirectory
        : System.IO.Directory.Exists(_workspacePath) ? _workspacePath
            : System.IO.Path.GetDirectoryName(_workspacePath) ?? Environment.CurrentDirectory;

    private void CreateTerminalSession()
    {
        try
        {
            EditorOptions settings = _editorOptions.Current;
            TerminalSessionOptions options = new() { WorkingDirectory = GetTerminalWorkingDirectory() };
            if (OperatingSystem.IsWindows())
            {
                options.Command = settings.ShellPath;
                if (!System.IO.File.Exists(options.Command)) throw new FileNotFoundException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        XamlVisualEditor.Core.Localization.UiStrings.ExecutableNotFound, options.Command));
            }
            if (OperatingSystem.IsWindows() && settings.TerminalLocation == TerminalLocation.External)
            {
                if (_terminalService is null) throw new InvalidOperationException(UiStrings.TerminalServiceUnavailable);
                _terminalService.StartExternalSession(options);
                StatusText = XamlVisualEditor.Core.Localization.UiStrings.TerminalExternalStarted;
            }
            else CreateTerminalSession(options);
        }
        catch (Exception error)
        {
            SetPersistentStatus(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                XamlVisualEditor.Core.Localization.UiStrings.TerminalStartFailed, error.Message));
            _logger.LogError(error, "Terminal startup failed");
        }
    }
    private void OnDebugOutputReceived(DebugOutputEvent output)
    {
        string level = output.Category switch
        {
            DebugOutputCategory.StdErr => "Error",
            DebugOutputCategory.StdOut => "Info",
            DebugOutputCategory.Telemetry => "Info",
            _ => "Debug"
        };

        string text = output.Text.TrimEnd();
        if (text.Length == 0)
        {
            return;
        }

        LogOutput(level, text);
    }

    private void OnDebugStopped(DebugStoppedEvent stopped)
    {
        SetPersistentStatus(stopped.Description is null
            ? string.Format(UiStrings.DebugPausedReason, stopped.Reason)
            : string.Format(UiStrings.DebugPausedDescription, stopped.Reason, stopped.Description));

        UpdateExecutionLocationFromFrame(Debugger.CallStack.SelectedFrame);
    }

    private void OnDebugContinued(DebugContinuedEvent continued)
    {
        SetPersistentStatus(UiStrings.Running);
        ClearExecutionLocation();
    }

    private void UpdateExecutionLocationFromFrame(StackFrameViewModel? frame)
    {
        if (frame is null || frame.Line is null || string.IsNullOrWhiteSpace(frame.FilePath))
        {
            return;
        }

        SetExecutionLocation(frame.FilePath, frame.Line.Value);
    }

    private void SetExecutionLocation(string filePath, int line)
    {
        foreach (IEditorDocumentViewModel doc in Documents)
        {
            if (doc is DesignerDocumentViewModel designer)
            {
                designer.CodeEditor.ExecutionLine = FileSystemPathComparison.Equals(designer.FilePath, filePath)
                    ? line
                    : null;
            }
            else if (doc is TextDocumentViewModel text)
            {
                text.ExecutionLine = FileSystemPathComparison.Equals(text.FilePath, filePath)
                    ? line
                    : null;
            }
        }
    }

    private void ClearExecutionLocation()
    {
        foreach (IEditorDocumentViewModel doc in Documents)
        {
            if (doc is DesignerDocumentViewModel designer)
            {
                designer.CodeEditor.ExecutionLine = null;
            }
            else if (doc is TextDocumentViewModel text)
            {
                text.ExecutionLine = null;
            }
        }
    }

    private void ToggleBreakpointAtCaret()
    {
        if (ActiveDocument is null)
        {
            return;
        }

        string filePath = ActiveDocument.FilePath;
        int line = ActiveDocument.CurrentLine;
        if (line <= 0)
        {
            return;
        }

        Breakpoints.ToggleBreakpoint(filePath, line, ActiveDocument.CurrentColumn);
    }

    private void InitializeDebuggerServices()
    {
        _adapterPathsByService[NetcoredbgServiceId] = DebuggerAdapterPath;
        _activeDebuggerServiceId = _debuggerRegistry.ActiveServiceId;
        UpdateAdapterPathForActiveService();

        _debuggerRegistry.Changed += OnDebuggerRegistryChanged;
        _disposables.Add(Disposable.Create(() => _debuggerRegistry.Changed -= OnDebuggerRegistryChanged));

        IDisposable adapterPathSubscription = this.WhenAnyValue(x => x.DebuggerAdapterPath)
            .Skip(1)
            .Subscribe(StoreAdapterPathForActiveService);
        _disposables.Add(adapterPathSubscription);
    }

    private void OnDebuggerRegistryChanged(object? sender, EventArgs e)
    {
        string? currentId = _debuggerRegistry.ActiveServiceId;
        if (!string.Equals(_activeDebuggerServiceId, currentId, StringComparison.Ordinal))
        {
            _activeDebuggerServiceId = currentId;
            UpdateAdapterPathForActiveService();
        }
    }

    private void UpdateAdapterPathForActiveService()
    {
        string? resolved = GetAdapterPathForService(_activeDebuggerServiceId);
        string nextValue = resolved ?? string.Empty;
        if (!string.Equals(DebuggerAdapterPath, nextValue, StringComparison.Ordinal))
        {
            DebuggerAdapterPath = nextValue;
        }
    }

    private string? GetAdapterPathForService(string? serviceId)
    {
        if (!string.IsNullOrWhiteSpace(serviceId)
            && _adapterPathsByService.TryGetValue(serviceId, out string? stored)
            && !string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        DebuggerServiceRegistration? registration = _debuggerRegistry.Services
            .FirstOrDefault(service => string.Equals(service.Id, serviceId, StringComparison.Ordinal));
        string? resolved = registration?.AdapterLocator?.ResolveAdapterPath();
        if (!string.IsNullOrWhiteSpace(resolved) && !string.IsNullOrWhiteSpace(serviceId))
        {
            _adapterPathsByService[serviceId] = resolved;
        }

        return resolved;
    }

    private void StoreAdapterPathForActiveService(string? path)
    {
        string? serviceId = _activeDebuggerServiceId ?? _debuggerRegistry.ActiveServiceId;
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return;
        }

        _adapterPathsByService[serviceId] = string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static bool IsDebugIdle(DebugSessionState state)
    {
        return state == DebugSessionState.Created
            || state == DebugSessionState.Terminated
            || state == DebugSessionState.Failed
            || state == DebugSessionState.Stopped;
    }

    private async System.Threading.Tasks.Task StartDebuggingAsync()
    {
        if (_workspace is null)
        {
            StatusText = UiStrings.DebugNeedsWorkspace;
            return;
        }

        if (!Debugger.HasDebuggerService)
        {
            StatusText = UiStrings.DebuggerNotConfigured;
            return;
        }

        ProjectModel? project = ResolveActiveProject();
        if (project is null)
        {
            StatusText = UiStrings.NoDebugProject;
            return;
        }

        SetActiveProject(project);
        if (!project.IsExecutable)
        {
            StatusText = UiStrings.DebugProjectNotExecutable;
            LogOutput("Error", $"Debug start failed: {project.Name} is not an executable project.");
            return;
        }

        LogOutput("Info", $"Debug start requested: {project.Name} ({project.ProjectPath})");
        await EnsureProjectBuiltAsync(project, suppressWarnings: false);

        string? assemblyPath = ResolveTargetAssemblyPath(project);
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            StatusText = UiStrings.DebugAssemblyUnresolved;
            return;
        }

        if (!TryValidateDebugTarget(assemblyPath, out string? failureReason))
        {
            StatusText = UiStrings.DebugOutputNotRunnable;
            LogOutput("Error", failureReason ?? $"Debug start failed: '{assemblyPath}' is not runnable.");
            return;
        }

        string? workingDir = System.IO.Path.GetDirectoryName(assemblyPath);
        string? adapterPath = await ResolveDebuggerAdapterPathAsync();
        if (string.IsNullOrWhiteSpace(adapterPath))
        {
            DebuggerServiceRegistration? activeRegistration = _debuggerRegistry.GetActiveRegistration();
            string serviceName = activeRegistration?.DisplayName ?? "debugger adapter";
            StatusText = string.Format(UiStrings.DebugServiceMissing, serviceName);
            if (string.Equals(activeRegistration?.Id, NetcoredbgServiceId, StringComparison.Ordinal))
            {
                LogOutput("Error", AutoDownloadTools
                    ? $"Debug start failed: {serviceName} not found. Download was not completed."
                    : $"Debug start failed: {serviceName} not found. Set DebuggerAdapterPath or enable auto-download.");
            }
            else
            {
                LogOutput("Error", $"Debug start failed: {serviceName} not found. Set DebuggerAdapterPath or install the adapter.");
            }
            return;
        }

        DebugLaunchOptions options = new()
        {
            AdapterPath = adapterPath,
            ProgramPath = assemblyPath,
            Arguments = string.IsNullOrWhiteSpace(ProgramArguments) ? null : ProgramArguments,
            WorkingDirectory = workingDir,
            StopAtEntry = DebugStopAtEntry
        };

        LogOutput("Info", $"Debug adapter: {options.AdapterPath}");
        LogOutput("Info", $"Debug program: {options.ProgramPath}");
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            LogOutput("Info", $"Debug working dir: {options.WorkingDirectory}");
        }
        if (!string.IsNullOrWhiteSpace(options.Arguments))
        {
            LogOutput("Info", $"Debug arguments: {options.Arguments}");
        }

        try
        {
            await Debugger.StartAsync(options);
            SetPersistentStatus(string.Format(UiStrings.DebuggingFile, (object?)(System.IO.Path.GetFileName(assemblyPath))));
        }
        catch (Exception ex)
        {
            SetPersistentStatus(string.Format(UiStrings.DebugStartFailed, ex.Message));
            LogOutput("Error", $"Debug start failed: {ex.Message}");
        }
    }

    private static string? ResolveDebuggerAdapterPath(string adapterPath)
    {
        if (string.IsNullOrWhiteSpace(adapterPath))
        {
            return null;
        }

        if (System.IO.Path.IsPathRooted(adapterPath))
        {
            return System.IO.File.Exists(adapterPath) ? adapterPath : null;
        }

        string baseDir = AppContext.BaseDirectory;
        string localPath = System.IO.Path.Combine(baseDir, adapterPath);
        if (System.IO.File.Exists(localPath))
        {
            return localPath;
        }

        try
        {
            string cwd = System.IO.Directory.GetCurrentDirectory();
            string cwdPath = System.IO.Path.Combine(cwd, adapterPath);
            if (System.IO.File.Exists(cwdPath))
            {
                return cwdPath;
            }
        }
        catch
        {
        }

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            return null;
        }

        foreach (string dir in pathVar.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = System.IO.Path.Combine(dir, adapterPath);
                if (System.IO.File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private async System.Threading.Tasks.Task<string?> ResolveDebuggerAdapterPathAsync()
    {
        string? adapterPath = ResolveDebuggerAdapterPath(DebuggerAdapterPath);
        if (!string.IsNullOrWhiteSpace(adapterPath))
        {
            return adapterPath;
        }

        DebuggerServiceRegistration? activeRegistration = _debuggerRegistry.GetActiveRegistration();
        string? resolved = activeRegistration?.AdapterLocator?.ResolveAdapterPath();
        adapterPath = string.IsNullOrWhiteSpace(resolved) ? null : ResolveDebuggerAdapterPath(resolved);
        if (!string.IsNullOrWhiteSpace(adapterPath))
        {
            DebuggerAdapterPath = adapterPath;
            return adapterPath;
        }

        if (activeRegistration?.Id != NetcoredbgServiceId)
        {
            return null;
        }

        if (!AutoDownloadTools || _debugToolInstaller is null)
        {
            return null;
        }

        try
        {
            LogOutput("Info", UiStrings.NetcoredbgDownloadStarting);
            string? downloaded = await _debugToolInstaller.EnsureNetcoredbgAsync(ConfirmDebugToolConsentAsync);
            if (!string.IsNullOrWhiteSpace(downloaded))
            {
                DebuggerAdapterPath = downloaded;
                return downloaded;
            }

            LogOutput("Info", UiStrings.NetcoredbgDownloadCanceled);
        }
        catch (Exception ex)
        {
            LogOutput("Error", $"netcoredbg download failed: {ex.Message}");
            SetPersistentStatus(string.Format(UiStrings.DebuggerDownloadFailed, ex.Message));
        }

        return null;
    }

    private ProjectModel? ResolveActiveProject()
    {
        if (ActiveProject is not null)
        {
            return ActiveProject;
        }

        if (!string.IsNullOrWhiteSpace(ActiveProjectPath) &&
            _projectLookup.TryGetValue(ActiveProjectPath, out ProjectModel? activeByPath))
        {
            return activeByPath;
        }

        if (SolutionExplorer.SelectedNode?.Kind == SolutionExplorerNodeKind.Project &&
            SolutionExplorer.SelectedNode.Project is not null)
        {
            SetActiveProject(SolutionExplorer.SelectedNode.Project);
        }

        if (_workspace is null)
        {
            return null;
        }

        if (ActiveDocument is not null)
        {
            ProjectModel? docProject = ProjectSelection.FindProjectForFile(_workspace, ActiveDocument.FilePath);
            if (docProject is not null)
            {
                return docProject;
            }
        }

        if (WorkspaceProjects.Count > 0)
        {
            return ProjectSelection.SelectPreferredProject(WorkspaceProjects);
        }

        return _workspace.Projects.FirstOrDefault();
    }

    private void SetActiveProject(ProjectModel? project)
    {
        ProjectModel? previous = ActiveProject;

        if (project is null)
        {
            ActiveProject = null;
            ActiveProjectPath = null;
            this.RaisePropertyChanged(nameof(ActiveProjectName));
            SolutionExplorer.SetStartupProject(null);
            if (!IsWorkspaceLoading) RefreshControlCatalog();
            if (previous is not null)
            {
                StatusText = UiStrings.StartupProjectCleared;
                LogOutput("Info", $"Startup project cleared (was {previous.Name}).");
            }
            SaveWorkspaceSession();
            return;
        }

        bool changed = previous is null
                       || !FileSystemPathComparison.Equals(previous.ProjectPath, project.ProjectPath)
                       || !string.Equals(previous.TargetFramework, project.TargetFramework, StringComparison.OrdinalIgnoreCase);

        ActiveProject = project;
        ActiveProjectPath = project.ProjectPath;
        if (changed && !IsWorkspaceLoading) RefreshControlCatalog();
        this.RaisePropertyChanged(nameof(ActiveProjectName));
        StatusText = string.Format(UiStrings.StartupProjectStatus, project.Name);
        if (changed)
        {
            string previousName = previous?.Name ?? "(none)";
            string previousTfm = previous?.TargetFramework ?? "unknown";
            string currentTfm = project.TargetFramework ?? "unknown";
            LogOutput(
                "Info",
                $"Startup project changed: {previousName} ({previousTfm}) -> {project.Name} ({currentTfm}) [{project.ProjectPath}]");
        }
        SolutionExplorer.SetStartupProject(project);
        SaveWorkspaceSession();
    }

    // Saves the startup project and the open documents of the loaded workspace. Loading, closing and restoring a
    // workspace change both on their own and must not overwrite the saved session.
    private void SaveWorkspaceSession()
    {
        if (!HasWorkspace || _workspacePath is not { } workspacePath || IsWorkspaceLoading || _isRestoringWorkspaceSession)
        {
            return;
        }

        string[] openDocuments = Documents
            .Select(document => document.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
            .ToArray();
        _workspaceSessions.Save(workspacePath, new WorkspaceSession
        {
            StartupProjectPath = ActiveProject?.ProjectPath,
            StartupTargetFramework = ActiveProject?.TargetFramework,
            OpenDocuments = openDocuments,
            ActiveDocument = ActiveDocument?.FilePath is { } active && openDocuments.Contains(active, FileSystemPathComparison.Comparer)
                ? active : null
        });
    }

    // Reopens the documents of the previous session in their order and restores the startup project. The documents
    // open without the open interceptors: the user asked for them in the earlier session.
    private async System.Threading.Tasks.Task RestoreWorkspaceSessionAsync(string workspacePath, WorkspaceSession? session)
    {
        if (session is null || !FileSystemPathComparison.Equals(_workspacePath, workspacePath))
        {
            return;
        }

        _isRestoringWorkspaceSession = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(session.StartupProjectPath)
                && ResolveProjectByPath(session.StartupProjectPath, session.StartupTargetFramework) is { } startup)
            {
                SetActiveProject(startup);
            }

            IEditorDocumentViewModel? active = null;
            foreach (string path in session.OpenDocuments)
            {
                if (!FileSystemPathValidation.IsAccepted(path) || !System.IO.File.Exists(path)) continue;
                IEditorDocumentViewModel? document = await EnsureDocumentOpenAsync(path, addRecent: false, updateStatus: false,
                    allowWorkspaceLoad: false);
                if (!FileSystemPathComparison.Equals(_workspacePath, workspacePath)) return;
                if (document is not null && FileSystemPathComparison.Equals(path, session.ActiveDocument)) active = document;
            }

            if (active is not null && Documents.Contains(active)) ActiveDocument = active;
        }
        finally
        {
            _isRestoringWorkspaceSession = false;
        }

        SaveWorkspaceSession();
    }

    private void SetActiveProjectByPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return;
        }

        ActiveProjectPath = projectPath;

        ProjectModel? project = ResolveProjectByPath(projectPath);
        if (project is not null)
        {
            SetActiveProject(project);
            return;
        }

        SolutionExplorer.SetStartupProjectPath(projectPath);
    }

    private ProjectModel? ResolveProjectByPath(string projectPath, string? targetFramework = null)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(targetFramework) && _workspace is not null)
        {
            foreach (ProjectModel candidate in _workspace.Projects)
            {
                if (FileSystemPathComparison.Equals(candidate.ProjectPath, projectPath)
                    && string.Equals(candidate.TargetFramework, targetFramework, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        if (_projectLookup.TryGetValue(projectPath, out ProjectModel? fromLookup))
        {
            return fromLookup;
        }

        if (_workspace is null)
        {
            return null;
        }

        ProjectModel? match = null;
        foreach (ProjectModel candidate in _workspace.Projects)
        {
            if (!FileSystemPathComparison.Equals(candidate.ProjectPath, projectPath))
            {
                continue;
            }

            match = match is null
                ? candidate
                : ProjectSelection.ChoosePreferredProject(match, candidate);
        }

        return match;
    }

    private async System.Threading.Tasks.Task EnsureProjectBuiltAsync(ProjectModel project, bool suppressWarnings)
    {
        string? output = ResolveTargetAssemblyPath(project);
        if (!string.IsNullOrWhiteSpace(output) && System.IO.File.Exists(output))
        {
            return;
        }

        if (!suppressWarnings)
        {
            LogOutput("Info", $"Building {project.Name}...");
        }

        await RunDotNetCommandAsync(project.ProjectPath, "build");
    }

    private async System.Threading.Tasks.Task StopDebuggingAsync()
    {
        try
        {
            await Debugger.StopAsync();
            StatusText = UiStrings.DebuggingStopped;
            ClearExecutionLocation();
        }
        catch (Exception ex)
        {
            SetPersistentStatus(string.Format(UiStrings.DebugStopFailed, ex.Message));
        }
    }

    private async System.Threading.Tasks.Task StartRunAsync()
    {
        if (_workspace is null)
        {
            StatusText = UiStrings.RunNeedsWorkspace;
            return;
        }

        ProjectModel? project = ResolveActiveProject();
        if (project is null)
        {
            StatusText = UiStrings.NoRunProject;
            return;
        }

        if (_runProcess is not null && !_runProcess.HasExited)
        {
            StatusText = UiStrings.ProgramAlreadyRunning;
            return;
        }

        SetActiveProject(project);
        if (!project.IsExecutable)
        {
            StatusText = UiStrings.RunProjectNotExecutable;
            LogOutput("Error", $"Run failed: {project.Name} is not an executable project.");
            return;
        }

        LogOutput("Info", $"Run requested: {project.Name} ({project.ProjectPath})");
        await EnsureProjectBuiltAsync(project, suppressWarnings: false);

        string? runAssemblyPath = ResolveTargetAssemblyPath(project);
        if (string.IsNullOrWhiteSpace(runAssemblyPath))
        {
            StatusText = UiStrings.RunAssemblyUnresolved;
            return;
        }

        if (!TryValidateDebugTarget(runAssemblyPath, out string? runFailure))
        {
            StatusText = UiStrings.RunOutputNotRunnable;
            LogOutput("Error", runFailure ?? UiStrings.RunOutputNotRunnable);
            return;
        }

        string? workingDirectory = System.IO.Path.GetDirectoryName(project.ProjectPath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            StatusText = UiStrings.RunDirectoryUnresolved;
            return;
        }

        string args = BuildRunArguments(project.ProjectPath, ProgramArguments);
        System.Diagnostics.ProcessStartInfo startInfo = new()
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        System.Diagnostics.Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                LogOutput("Info", e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                LogOutput("Error", e.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            IsRunActive = false;
            StatusText = UiStrings.ProgramExited;
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _runProcess = process;
            IsRunActive = true;
            SetPersistentStatus(string.Format(UiStrings.RunningProject, project.Name));
            LogOutput("Info", $"Run command: dotnet {args}");
        }
        catch (Exception ex)
        {
            SetPersistentStatus(string.Format(UiStrings.RunFailed, ex.Message));
            LogOutput("Error", ex.Message);
        }
    }

    private System.Threading.Tasks.Task StopRunAsync()
    {
        if (_runProcess is null)
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }

        try
        {
            if (!_runProcess.HasExited)
            {
                _runProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            LogOutput("Error", $"Stop run failed: {ex.Message}");
        }
        finally
        {
            _runProcess.Dispose();
            _runProcess = null;
            IsRunActive = false;
            StatusText = UiStrings.ProgramStopped;
        }

        return System.Threading.Tasks.Task.CompletedTask;
    }

    private static string BuildRunArguments(string projectPath, string? programArguments)
    {
        if (string.IsNullOrWhiteSpace(programArguments))
        {
            return $"run --project \"{projectPath}\"";
        }

        return $"run --project \"{projectPath}\" -- {programArguments}";
    }

    internal static string? ResolveTargetAssemblyPath(ProjectModel project)
    {
        return ProjectSelection.ResolveTargetAssemblyPath(project);
    }

    internal static bool TryValidateDebugTarget(string assemblyPath, out string? failureReason)
    {
        if (IsUnsupportedFrameworkTarget(assemblyPath))
        {
            failureReason = $"Debug start failed: '{assemblyPath}' targets netstandard or .NET Framework. Select an executable startup project or a net6+ target.";
            return false;
        }

        string runtimeConfigPath = System.IO.Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        if (!System.IO.File.Exists(runtimeConfigPath))
        {
            failureReason = $"Debug start failed: '{assemblyPath}' has no runtimeconfig.json. Select a runnable startup project (Exe output).";
            return false;
        }

        failureReason = null;
        return true;
    }

    private static bool IsUnsupportedFrameworkTarget(string assemblyPath)
    {
        string normalized = assemblyPath.Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("/netstandard") || normalized.Contains("/net4");
    }


    private void EnsureCanvasDocument()
    {
        if (DockLayout is null)
        {
            return;
        }

        InfiniteCanvasDocument? existing = XamlEditorDockFactory.FindDockable<InfiniteCanvasDocument>(DockLayout, "InfiniteCanvas");
        if (existing is not null)
        {
            existing.CanvasViewModel = InfiniteCanvas;
            _canvasDocument = existing;
            return;
        }

        _canvasDocument = DockFactory.AddCanvasDocument(DockLayout, InfiniteCanvas);
    }

    private void ShowCanvasDocument()
    {
        EnsureCanvasDocument();
        if (_canvasDocument is not null)
        {
            DockFactory.SetActiveDockable(_canvasDocument);
        }
    }

    private void UpdateTrees(DesignerDocumentViewModel? doc)
    {
        if (AreExtensionTreePanelsVisible())
        {
            if (VisualTree.Root is not null)
            {
                VisualTree.SetRoot(null);
            }

            if (LogicalTree.Root is not null)
            {
                LogicalTree.SetRoot(null);
            }

            return;
        }

        if (doc is null)
        {
            VisualTree.SetRoot(null);
            LogicalTree.SetRoot(null);
            return;
        }

        // Preserve expansion state before rebuilding
        _visualExpandedIds.Clear();
        VisualTree.Root?.CollectExpandedIds(_visualExpandedIds);
        _logicalExpandedIds.Clear();
        LogicalTree.Root?.CollectExpandedIds(_logicalExpandedIds);

        MutableAstDocument? astDoc = doc.SyncEngine.CurrentDocument;
        VisualTreeNodeViewModel? visualRoot = VisualTreeNodeViewModel.FromAstDocument(astDoc);
        LogicalTreeNodeViewModel? logicalRoot = LogicalTreeNodeViewModel.FromAstDocument(astDoc);

        if (visualRoot is not null && _visualExpandedIds.Count > 0)
        {
            visualRoot.ApplyExpandedIds(_visualExpandedIds);
        }

        if (logicalRoot is not null && _logicalExpandedIds.Count > 0)
        {
            logicalRoot.ApplyExpandedIds(_logicalExpandedIds);
        }

        VisualTree.SetRoot(visualRoot);
        LogicalTree.SetRoot(logicalRoot);

        ApplySelectionToTrees(doc.SelectedNodeId);
    }

    private void ApplySelectionToTrees(Guid? nodeId)
    {
        if (AreExtensionTreePanelsVisible())
        {
            return;
        }

        _suppressTreeSelectionSync = true;
        try
        {
            if (nodeId is null)
            {
                VisualTree.SelectNode(null);
                LogicalTree.SelectNode(null);
                return;
            }

            VisualTreeNodeViewModel? visualNode = VisualTree.Root?.FindByNodeId(nodeId.Value);
            if (visualNode is not null)
            {
                visualNode.ExpandPathToNode(nodeId.Value);
            }
            VisualTree.SelectNode(visualNode);

            LogicalTreeNodeViewModel? logicalNode = LogicalTree.Root?.FindByNodeId(nodeId.Value);
            if (logicalNode is not null)
            {
                logicalNode.ExpandPathToNode(nodeId.Value);
            }
            LogicalTree.SelectNode(logicalNode);
        }
        finally
        {
            _suppressTreeSelectionSync = false;
        }
    }

    private bool AreExtensionTreePanelsVisible()
    {
        if (_extensionViews.ContainsKey("visualTree.panel")
            || _extensionViews.ContainsKey("logicalTree.panel"))
        {
            return true;
        }

        return IsExtensionViewVisible("visualTree.panel")
            || IsExtensionViewVisible("logicalTree.panel");
    }

    private void AddDocumentToDock(IEditorDocumentViewModel document)
    {
        if (DockLayout is null)
        {
            return;
        }

        IDockable? dockDoc = document switch
        {
            DesignerDocumentViewModel designer => DockFactory.AddDocument(DockLayout, designer),
            TextDocumentViewModel text => DockFactory.AddTextDocument(DockLayout, text),
            _ => null
        };
        if (dockDoc is DesignerDocument designerDock) designerDock.TabCommands = DocumentTabCommands;
        else if (dockDoc is TextDocument textDock) textDock.TabCommands = DocumentTabCommands;

        if (dockDoc is not null)
        {
            _dockDocuments[document.FilePath] = dockDoc;
            UpdateDockTitle(document, dockDoc);
            SubscribeDockTitleUpdates(document, dockDoc);
        }
    }

    private void UpdateWindowTitle() =>
        Title = DocumentTitle.FormatWindow(ActiveDocument?.FileName, ActiveDocument?.FilePath,
            _editorOptions.Current.ShowFullPathInTitle);

    private void UpdateDockTitle(IEditorDocumentViewModel document, IDockable dockDoc)
    {
        dockDoc.Title = GetDockTitle(document);
    }

    private string GetDockTitle(IEditorDocumentViewModel document)
    {
        return document switch
        {
            DesignerDocumentViewModel designer => designer.Title,
            _ => DocumentTitle.Format(document.FileName, document.IsModified, document.IsReadOnlyFile)
        };
    }

    private void SubscribeDockTitleUpdates(IEditorDocumentViewModel document, IDockable dockDoc)
    {
        if (document is not INotifyPropertyChanged notifying)
        {
            return;
        }

        if (_dockTitleSubscriptions.TryGetValue(document, out IDisposable? existing))
        {
            existing.Dispose();
        }

        IDisposable subscription = Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => notifying.PropertyChanged += h,
                h => notifying.PropertyChanged -= h)
            .Where(e => string.IsNullOrEmpty(e.EventArgs.PropertyName) ||
                        e.EventArgs.PropertyName is nameof(IEditorDocumentViewModel.IsModified)
                            or nameof(IEditorDocumentViewModel.IsReadOnlyFile))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => UpdateDockTitle(document, dockDoc));

        _dockTitleSubscriptions[document] = subscription;
    }

    private void SetActiveDockDocument(IEditorDocumentViewModel document)
    {
        if (DockLayout is null)
        {
            return;
        }

        if (_dockDocuments.TryGetValue(document.FilePath, out IDockable? dockDoc))
        {
            DockFactory.SetActiveDockable(dockDoc);
        }
    }

    private void CloseDockDocument(IEditorDocumentViewModel document)
    {
        if (DockLayout is null)
        {
            return;
        }

        if (_dockDocuments.TryGetValue(document.FilePath, out IDockable? dockDoc))
        {
            DockFactory.CloseDockable(dockDoc);
            _dockDocuments.Remove(document.FilePath);
        }
    }

    private void WireDockEvents()
    {
        DockFactory.ActiveDockableChanged += OnActiveDockableChanged;
        DockFactory.DockableClosing += OnDockableClosing;
        DockFactory.DockableClosed += OnDockableClosed;
    }

    private void OnActiveDockableChanged(object? sender, ActiveDockableChangedEventArgs e)
    {
        string? nextActiveExtensionViewId = null;
        switch (e.Dockable)
        {
            case DesignerDocument designer:
                if (!designer.DocumentViewModel.IsDisposed)
                {
                    ActiveDocument = designer.DocumentViewModel;
                }
                break;
            case TextDocument text:
                if (!text.DocumentViewModel.IsDisposed)
                {
                    ActiveDocument = text.DocumentViewModel;
                }
                break;
            case ExtensionTool extensionTool when !string.IsNullOrWhiteSpace(extensionTool.ViewId):
                nextActiveExtensionViewId = extensionTool.ViewId;
                break;
        }

        UpdateActiveExtensionViewFocus(nextActiveExtensionViewId);
    }

    private void OnDockableClosed(object? sender, DockableClosedEventArgs e)
    {
        if (e.Dockable is DesignerDocument designer)
        {
            _isClosingFromDock = true;
            try
            {
                CloseDocument(designer.DocumentViewModel);
            }
            finally
            {
                _isClosingFromDock = false;
            }
            return;
        }

        if (e.Dockable is TextDocument text)
        {
            _isClosingFromDock = true;
            try
            {
                CloseDocument(text.DocumentViewModel);
            }
            finally
            {
                _isClosingFromDock = false;
            }
            return;
        }

        if (e.Dockable is TerminalTool terminalTool && terminalTool.TerminalViewModel is TerminalViewModel terminalVm)
        {
            RemoveTerminalSession(terminalVm);
            _terminalTools.Remove(terminalVm.Id);
            terminalTool.TerminalViewModel = null;
            if (terminalTool.Owner is IDock dock && dock.VisibleDockables is not null && dock.VisibleDockables.Contains(terminalTool))
            {
                dock.VisibleDockables.Remove(terminalTool);
            }
            return;
        }

        if (e.Dockable is ExtensionTool extensionTool && !string.IsNullOrWhiteSpace(extensionTool.ViewId))
        {
            string viewId = extensionTool.ViewId;
            RaiseExtensionViewVisibilityChanged(viewId, false);
            if (string.Equals(_activeExtensionViewId, viewId, StringComparison.OrdinalIgnoreCase))
            {
                RaiseExtensionViewFocusChanged(viewId, false);
                _activeExtensionViewId = null;
            }
        }
    }

    private void RemoveTerminalSession(TerminalViewModel terminalVm)
    {
        if (_terminalTitleSubscriptions.Remove(terminalVm.Id, out IDisposable? subscription))
        {
            subscription.Dispose();
        }

        terminalVm.Dispose();
        Terminals.Remove(terminalVm);
    }

    private void SetDockableVisibility(string id, bool isVisible)
    {
        if (DockLayout is null)
        {
            return;
        }

        IDockable? dockable = XamlEditorDockFactory.FindDockable<IDockable>(DockLayout, id)
            ?? (isVisible ? RecreateDebuggerTool(id) : null);
        if (dockable is null)
        {
            string? extensionViewId = MapLegacyToolToExtensionView(id);
            if (!string.IsNullOrWhiteSpace(extensionViewId)
                && TrySetExtensionDockableVisibility(extensionViewId, isVisible))
            {
                return;
            }
            return;
        }

        SetToolVisibility(dockable, isVisible);
        if (isVisible && !IsDockableShown(id))
        {
            FloatTool(dockable);
        }
    }

    // Closing a debugger tool in the dock removes it from the layout. The view menu opens a new one, which joins the
    // hidden tools, so SetToolVisibility moves it into its home dock like a hidden tool of a restored layout.
    private IDockable? RecreateDebuggerTool(string id)
    {
        Tool? tool = id switch
        {
            "Breakpoints" => new BreakpointsTool(Breakpoints),
            "CallStack" => new CallStackTool(CallStack),
            "Locals" => new LocalsTool(Locals),
            "Watches" => new WatchesTool(Watches),
            _ => null
        };
        if (tool is null || DockLayout is null)
        {
            return null;
        }

        DockLayout.HiddenDockables ??= DockFactory.CreateList<IDockable>();
        tool.Owner = DockLayout;
        DockLayout.HiddenDockables.Add(tool);
        return tool;
    }

    private void SetToolVisibility(IDockable dockable, bool isVisible)
    {
        if (isVisible)
        {
            PlaceHiddenToolWithoutDock(dockable);
        }

        if (DockFactory.IsDockablePinned(dockable, DockLayout))
        {
            if (isVisible)
                DockFactory.PreviewPinnedDockable(dockable);
            else if (DockLayout.PinnedDock?.VisibleDockables?.Contains(dockable) == true)
                DockFactory.HidePreviewingDockables(DockLayout);
            return;
        }

        if (isVisible)
        {
            DockFactory.RestoreDockable(dockable);
            XamlEditorDockFactory.RestoreSideDockWidth(dockable);
            DockFactory.SetActiveDockable(dockable);
            if (dockable.Owner is IDock owner && owner is not IRootDock)
                DockFactory.SetFocusedDockable(owner, dockable);
        }
        else
        {
            DockFactory.HideDockable(dockable);
        }
    }

    // A restored layout stores hidden tools with the root dock as owner, so restoring them would put them into the
    // root where nothing renders them. Such a tool moves into its home tool dock instead: the dock of its extension
    // location, or the bottom dock for the debugger tools. A collapsed bottom dock keeps its tools pinned, so the
    // tool joins them there.
    private void PlaceHiddenToolWithoutDock(IDockable dockable)
    {
        if (DockLayout?.HiddenDockables is not { } hidden
            || !hidden.Contains(dockable)
            || dockable.Owner is not (null or IRootDock))
        {
            return;
        }

        string dockId = dockable is ExtensionTool { ExtensionViewModel: { } view }
            ? view.Location switch
            {
                ExtensionViewLocation.Left => "LeftToolDock",
                ExtensionViewLocation.Bottom => "BottomToolDock",
                _ => "RightToolDock"
            }
            : "BottomToolDock";
        IDock? home = XamlEditorDockFactory.FindDockable<IToolDock>(DockLayout, dockId)
            ?? XamlEditorDockFactory.FindDockables<IToolDock>(DockLayout).FirstOrDefault(dock => dock is not IRootDock);
        hidden.Remove(dockable);
        if (home is null)
        {
            FloatTool(dockable);
            return;
        }

        DockFactory.AddDockable(home, dockable);
        dockable.Owner = home;
        if (home is ToolDock { Proportion: 0 } && string.Equals(home.Id, "BottomToolDock", StringComparison.Ordinal))
        {
            DockFactory.PinDockable(dockable);
            dockable.GetPinnedBounds(out _, out _, out double width, out double height);
            if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
            {
                dockable.SetPinnedBounds(0, 0, 400, 300);
            }
        }
    }

    // Last resort of the view menu: a tool that no dock can show opens in its own window.
    private void FloatTool(IDockable dockable)
    {
        if (DockLayout is null)
        {
            return;
        }

        DockLayout.HiddenDockables?.Remove(dockable);
        if (dockable.Owner is IDock owner && owner.VisibleDockables?.Contains(dockable) == true)
        {
            owner.VisibleDockables.Remove(dockable);
        }

        DockFactory.SplitToWindow(DockLayout, dockable, 200, 200, 600, 400);
    }

    private static string? MapLegacyToolToExtensionView(string id)
    {
        return id switch
        {
            "Toolbox" => "toolbox.panel",
            "SolutionExplorer" => "solutionExplorer.panel",
            "Properties" => "propertyEditor.panel",
            "Output" => "output.panel",
            "VisualTree" => "visualTree.panel",
            "LogicalTree" => "logicalTree.panel",
            "References" => "references.panel",
            "AnimationEditor" => "animationEditor.panel",
            "Collaboration" => "collaboration.panel",
            "DebugSettings" => "debugSettings.panel",
            "LspSettings" => "lspSettings.panel",
            _ => null
        };
    }

    public void ShowExtensionView(string viewId)
    {
        if (TryResolveExtensionViewId(viewId, out string resolved))
        {
            TrySetExtensionDockableVisibility(resolved, true);
        }
    }

    // A view that is open behind another tab of its dock is brought to the front; only the active tab is hidden.
    public void ToggleExtensionView(string viewId)
    {
        if (TryResolveExtensionViewId(viewId, out string resolved))
        {
            bool isVisible = IsExtensionViewVisible(resolved);
            if (isVisible && !IsExtensionViewFrontmost(resolved))
            {
                ActivateExtensionView(resolved);
                return;
            }

            TrySetExtensionDockableVisibility(resolved, !isVisible);
        }
    }

    // A tool closed with its tab button leaves the layout but stays in the lookup; it counts as missing, so
    // showing the view creates a new tool instead of restoring one that no dock contains.
    private ExtensionTool? FindExtensionTool(string viewId)
    {
        if (DockLayout is null)
        {
            return null;
        }

        ExtensionTool? tool = XamlEditorDockFactory.FindDockable<ExtensionTool>(DockLayout, ExtensionTool.BuildId(viewId));
        if (tool is null)
        {
            _extensionTools.Remove(viewId);
        }

        return tool;
    }

    private bool IsExtensionViewFrontmost(string viewId)
    {
        if (DockLayout is null)
        {
            return false;
        }

        ExtensionTool? tool = FindExtensionTool(viewId);

        return tool is not null
            && (DockFactory.IsDockablePinned(tool, DockLayout) || (tool.Owner is IDock dock && ReferenceEquals(dock.ActiveDockable, tool)));
    }

    public bool IsExtensionViewVisible(string viewId)
    {
        if (!TryResolveExtensionViewId(viewId, out string resolved))
        {
            return false;
        }

        if (DockLayout is null)
        {
            return false;
        }

        ExtensionTool? tool = FindExtensionTool(resolved);

        if (tool is not null && DockFactory.IsDockablePinned(tool, DockLayout))
        {
            return DockLayout.PinnedDock?.VisibleDockables?.Contains(tool) == true;
        }

        if (tool?.Owner is not IDock dock || dock.VisibleDockables is null)
        {
            return false;
        }

        return dock.VisibleDockables.Contains(tool);
    }

    public void ActivateExtensionView(string viewId)
    {
        if (!TryResolveExtensionViewId(viewId, out string resolved))
        {
            return;
        }

        if (DockLayout is null)
        {
            return;
        }

        ExtensionTool? tool = FindExtensionTool(resolved);

        if (tool is null && TrySetExtensionDockableVisibility(resolved, true))
        {
            _extensionTools.TryGetValue(resolved, out tool);
        }

        if (tool is null)
        {
            return;
        }

        bool wasVisible = IsExtensionViewVisible(resolved);
        SetToolVisibility(tool, true);
        if (!wasVisible)
        {
            RaiseExtensionViewVisibilityChanged(resolved, true);
        }
        UpdateActiveExtensionViewFocus(resolved);
    }

    private bool TrySetExtensionDockableVisibility(string viewId, bool isVisible)
    {
        if (DockLayout is null)
        {
            return false;
        }

        ExtensionTool? tool = FindExtensionTool(viewId);

        bool wasVisible = IsExtensionViewVisible(viewId);
        if (tool is null && isVisible && _extensionViews.TryGetValue(viewId, out ExtensionViewModel? viewModel))
        {
            tool = DockFactory.AddExtensionTool(DockLayout, viewModel);
            if (tool is null)
            {
                tool = new ExtensionTool(viewModel);
                FloatTool(tool);
            }

            _extensionTools[viewId] = tool;
        }

        if (tool is null)
        {
            return false;
        }

        SetToolVisibility(tool, isVisible);
        if (isVisible)
        {
            if (!IsExtensionViewVisible(viewId))
            {
                FloatTool(tool);
            }

            UpdateActiveExtensionViewFocus(viewId);
        }
        else
        {
            if (string.Equals(_activeExtensionViewId, viewId, StringComparison.OrdinalIgnoreCase))
            {
                UpdateActiveExtensionViewFocus(null);
            }
        }

        bool isVisibleNow = IsExtensionViewVisible(viewId);
        if (wasVisible != isVisibleNow)
        {
            RaiseExtensionViewVisibilityChanged(viewId, isVisibleNow);
        }

        return true;
    }

    private bool TryResolveExtensionViewId(string viewId, out string resolved)
    {
        if (_extensionViews.ContainsKey(viewId))
        {
            resolved = viewId;
            return true;
        }

        string? mapped = MapLegacyToolToExtensionView(viewId);
        if (!string.IsNullOrWhiteSpace(mapped))
        {
            resolved = mapped;
            return true;
        }

        resolved = viewId;
        return false;
    }

    private void UpdateActiveExtensionViewFocus(string? nextViewId)
    {
        if (string.Equals(_activeExtensionViewId, nextViewId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_activeExtensionViewId))
        {
            RaiseExtensionViewFocusChanged(_activeExtensionViewId, false);
        }

        _activeExtensionViewId = nextViewId;

        if (!string.IsNullOrWhiteSpace(_activeExtensionViewId))
        {
            RaiseExtensionViewFocusChanged(_activeExtensionViewId, true);
        }
    }

    private void RaiseExtensionViewVisibilityChanged(string viewId, bool isVisible)
    {
        ExtensionViewVisibilityChanged?.Invoke(
            this,
            new ExtensionViewVisibilityChangedEventArgs(viewId, isVisible));
    }

    private void RaiseExtensionViewFocusChanged(string viewId, bool isFocused)
    {
        ExtensionViewFocusChanged?.Invoke(
            this,
            new ExtensionViewFocusChangedEventArgs(viewId, isFocused));
    }

    /// <summary>
    /// Closes a document.
    /// </summary>
    public void CloseDocument(IEditorDocumentViewModel doc)
    {
        if (!_isClosingFromDock)
        {
            _isClosingDocument = true;
            try
            {
                CloseDockDocument(doc);
            }
            finally
            {
                _isClosingDocument = false;
            }
        }

        if (_dockTitleSubscriptions.Remove(doc, out IDisposable? subscription))
        {
            subscription.Dispose();
        }

        InfiniteCanvas.RemoveOpenDocumentItem(doc);
        Documents.Remove(doc);
        if (ReferenceEquals(ActiveDocument, doc))
        {
            ActiveDocument = Documents.FirstOrDefault();
        }
        DetachAutoSave(doc);
        if (doc is DesignerDocumentViewModel designer)
        {
            designer.CloseRequested -= CloseDocument;
            designer.PreviewerSession = null;
            _previewerActivationOrder.Remove(designer);
            _previewerLaunchService.StopPreviewer(designer.FilePath);
        }
        doc.Dispose();
        StatusText = string.Format(UiStrings.DocumentClosed, doc.FileName);
    }

    /// <summary>
    /// Opens a specific file.
    /// </summary>
    public System.Threading.Tasks.Task OpenFileAsync(string filePath)
    {
        return OpenFileAsync(filePath, allowWorkspaceLoad: true);
    }

    internal async System.Threading.Tasks.Task<IEditorDocumentViewModel?> OpenFileAsync(string filePath, bool allowWorkspaceLoad)
    {
        if (await ResolveUserOpenPathAsync(filePath) is not { } resolvedPath)
            return null;
        return await EnsureDocumentOpenAsync(resolvedPath, addRecent: true, updateStatus: true, allowWorkspaceLoad: allowWorkspaceLoad);
    }

    // Only opens the user asked for consult the interceptors; navigation into a file keeps the file itself.
    // Returns null when an interceptor cancelled the open.
    private async System.Threading.Tasks.Task<string?> ResolveUserOpenPathAsync(string filePath)
    {
        filePath = NormalizeAcceptedDocumentPath(filePath);

        if (_documentOpenResolver is null
            || !FileSystemPathValidation.IsAccepted(filePath)
            || !System.IO.File.Exists(filePath)
            || Documents.Any(document => FileSystemPathComparison.Equals(document.FilePath, filePath)))
        {
            return filePath;
        }

        try
        {
            return await _documentOpenResolver.ResolveAsync(filePath, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            StatusText = string.Format(UiStrings.OpenCanceled, (object?)(System.IO.Path.GetFileName(filePath)));
            return null;
        }
    }

    private async Task CloseWorkspaceAsync()
    {
        if (!await ConfirmCloseAsync(Documents.ToArray()))
        {
            return;
        }

        IsWorkspaceLoading = true;
        try
        {
            foreach (IEditorDocumentViewModel document in Documents.ToArray()) CloseDocument(document);
            _backNavigation.Clear();
            _forwardNavigation.Clear();
            UpdateNavigationState();
            await ResetWorkspaceStateAsync(workspacePath: null);
            StatusText = UiStrings.WorkspaceClosed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Workspace close failed: {Message}", ex.Message);
            SetPersistentStatus(string.Format(UiStrings.WorkspaceCloseFailed, ex.Message));
        }
        finally
        {
            IsWorkspaceLoading = false;
        }
    }

    public async System.Threading.Tasks.Task OpenFolderAsync(string folderPath)
    {
        if (RejectInvalidPath(folderPath))
        {
            RemoveRecentFile(folderPath, RecentEntryKind.Folder);
            return;
        }

        if (string.IsNullOrWhiteSpace(folderPath) || !System.IO.Directory.Exists(folderPath))
        {
            RemoveRecentFile(folderPath, RecentEntryKind.Folder);
            StatusText = UiStrings.FolderNotFound;
            return;
        }

        StopDocumentPreviewers();
        await ResetWorkspaceStateAsync(folderPath);

        string name = System.IO.Path.GetFileName(folderPath.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));
        StatusText = string.Format(UiStrings.FolderOpened, name);
        LogOutput("Info", $"Opened folder: {folderPath}");
        AddRecentFile(System.IO.Path.TrimEndingDirectorySeparator(folderPath), RecentEntryKind.Folder);
    }

    // Drops everything that belongs to the previous solution: projects, assemblies, design themes and language state.
    private async System.Threading.Tasks.Task ResetWorkspaceStateAsync(string? workspacePath)
    {
        _workspace = null;
        _workspacePath = workspacePath;
        _workspaceInfoUpdater?.UpdateWorkspacePath(workspacePath);
        HasWorkspace = false;
        _controlCatalogUpdater?.SetWorkspaceAssemblies(Array.Empty<string>());
        WorkspaceProjects.Clear();
        _projectLookup.Clear();
        SetActiveProject(null);
        SolutionExplorer.Clear();
        SolutionExplorer.IsVisible = false;
        _assemblyResolver?.Dispose();
        _assemblyResolver = null;
        WorkspaceDesignThemeRegistry.Clear();

        if (_languageRegistry is not null)
        {
            foreach (ILanguageIntellisenseService service in _languageRegistry.Services)
                await service.ClearWorkspaceAsync();
        }
    }

    private async System.Threading.Tasks.Task OpenFromSolutionExplorerAsync(string filePath)
    {
        if (await ResolveUserOpenPathAsync(filePath) is { } resolvedPath)
            await EnsureDocumentOpenAsync(resolvedPath, addRecent: true, updateStatus: true, allowWorkspaceLoad: false);
    }

    private async System.Threading.Tasks.Task OpenDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        if (paths.Count == 1)
        {
            await OpenFileAsync(paths[0]);
            return;
        }

        foreach (string path in paths.Distinct(FileSystemPathComparison.Comparer))
        {
            if (await ResolveUserOpenPathAsync(path) is { } resolvedPath)
                await EnsureDocumentOpenAsync(resolvedPath, addRecent: true, updateStatus: false, allowWorkspaceLoad: false);
        }

        StatusText = string.Format(UiStrings.FilesOpened, paths.Count);
    }

    private async System.Threading.Tasks.Task<IEditorDocumentViewModel?> EnsureDocumentOpenAsync(
        string filePath,
        bool addRecent,
        bool updateStatus,
        bool allowWorkspaceLoad)
    {
        if (RejectInvalidPath(filePath))
        {
            RemoveRecentFile(filePath);
            return null;
        }

        filePath = NormalizeDocumentPath(filePath);
        if (!System.IO.File.Exists(filePath))
        {
            RemoveRecentFile(filePath);
            StatusText = string.Format(UiStrings.FileNotFoundStatus, (object?)(System.IO.Path.GetFileName(filePath)));
            return null;
        }

        string extension = System.IO.Path.GetExtension(filePath);
        if (allowWorkspaceLoad && (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            await LoadWorkspaceAsync(filePath);
            if (addRecent && HasWorkspace && FileSystemPathComparison.Equals(_workspacePath, filePath))
            {
                AddRecentFile(filePath, RecentEntryKind.Workspace);
            }
            return null;
        }

        if (allowWorkspaceLoad && (extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase)))
        {
            await TryLoadWorkspaceForXamlAsync(filePath);
        }

        IEditorDocumentViewModel? existing = Documents.FirstOrDefault(d => FileSystemPathComparison.Equals(d.FilePath, filePath));
        if (existing is not null)
        {
            ActiveDocument = existing;
            SetActiveDockDocument(existing);
            if (addRecent) AddRecentFile(filePath);
            return existing;
        }

        IEditorDocumentViewModel doc;
        if (IsXamlFile(filePath))
        {
            DesignerDocumentViewModel designer = new(
                filePath,
                _metadataService,
                () => _workspace,
                OpenFileAsync,
                _loggerFactory?.CreateLogger<DesignerDocumentViewModel>(),
                _loggerFactory,
                _languageRegistry,
                StartPreviewerForDocumentAsync,
                UpdatePreviewerForDocumentAsync);
            designer.Breakpoints = Breakpoints;
            designer.CloseRequested += CloseDocument;
            ConfigureDesignerCommands(designer);
            Documents.Add(designer);
            ActiveDocument = designer;
            AddDocumentToDock(designer);
            AttachAutoSave(designer);
            try
            {
                await designer.LoadAsync();
            }
            catch (Exception ex) when (IsDocumentReadFailure(ex))
            {
                DiscardUnreadableDocument(designer, ex);
                return null;
            }
            if (designer.IsDisposed || !Documents.Contains(designer)) return null;
            UpdateTrees(designer);
            doc = designer;
        }
        else
        {
            TextDocumentViewModel textDoc = new(filePath, _languageRegistry);
            textDoc.Breakpoints = Breakpoints;
            Documents.Add(textDoc);
            ActiveDocument = textDoc;
            AddDocumentToDock(textDoc);
            AttachAutoSave(textDoc);
            try
            {
                await textDoc.LoadAsync();
            }
            catch (Exception ex) when (IsDocumentReadFailure(ex))
            {
                DiscardUnreadableDocument(textDoc, ex);
                return null;
            }
            UpdateTrees(null);
            doc = textDoc;
        }

        if (addRecent)
        {
            AddRecentFile(filePath);
        }

        if (updateStatus)
        {
            StatusText = string.Format(UiStrings.FileOpened, (object?)(System.IO.Path.GetFileName(filePath)));
        }

        return doc;
    }

    /// <summary>Normalizes a caller's path like an open does, so lookups find the open document; other paths stay as given.</summary>
    internal static string NormalizeAcceptedDocumentPath(string filePath) =>
        FileSystemPathValidation.IsAccepted(filePath) ? NormalizeDocumentPath(filePath) : filePath;

    // Document identity uses the full path, so one file reached through different spellings maps to one document.
    private static string NormalizeDocumentPath(string filePath)
    {
        try
        {
            return System.IO.Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return filePath;
        }
    }

    private static bool IsDocumentReadFailure(Exception error) =>
        error is DecoderFallbackException or System.IO.IOException or UnauthorizedAccessException;

    // The tab is added before the file is read; a file that cannot be read must not leave an empty tab behind.
    private void DiscardUnreadableDocument(IEditorDocumentViewModel document, Exception error)
    {
        _logger.LogWarning("Open document failed: {Path}: {Message}", document.FilePath, error.Message);
        string fileName = document.FileName;
        string reason = error is DecoderFallbackException
            ? UiStrings.DocumentNotUtf8Reason
            : FileSystemErrorMessages.DescribeReason(error, document.FilePath);
        CloseDocument(document);
        SetPersistentStatus(string.Format(UiStrings.DocumentOpenFailedNamed, fileName, reason));
    }

    private async Task OpenRecentAsync(RecentFileEntry entry)
    {
        if (entry.Kind == RecentEntryKind.Folder)
        {
            await OpenFolderAsync(entry.FilePath);
        }
        else if (entry.Kind == RecentEntryKind.Workspace)
        {
            if (!System.IO.File.Exists(entry.FilePath))
            {
                RemoveRecentFile(entry.FilePath);
                StatusText = string.Format(UiStrings.FileNotFoundStatus, entry.DisplayName);
                return;
            }
            await LoadWorkspaceAsync(entry.FilePath);
            if (HasWorkspace && FileSystemPathComparison.Equals(_workspacePath, entry.FilePath))
                AddRecentFile(entry.FilePath, entry.Kind);
        }
        else
        {
            await OpenFileAsync(entry.FilePath, allowWorkspaceLoad: false);
        }
    }

    private void LoadRecentFiles()
    {
        _isLoadingRecentFiles = true;
        int rejectedEntries = 0;
        try
        {
            if (_recentFilesPath is not { } path || !System.IO.File.Exists(path))
            {
                return;
            }

            string json = ReadRecentFilesText(path);
            using JsonDocument recent = JsonDocument.Parse(json);
            if (recent.RootElement.ValueKind != JsonValueKind.Array) return;

            HashSet<string> seen = new(FileSystemPathComparison.Comparer);
            foreach (JsonElement element in recent.RootElement.EnumerateArray())
            {
                RecentFileEntry? entry;
                if (element.ValueKind == JsonValueKind.String)
                {
                    // An older history holds paths only, so classify the entry once by its target.
                    string? filePath = element.GetString();
                    if (string.IsNullOrWhiteSpace(filePath)) continue;
                    string extension = System.IO.Path.GetExtension(filePath);
                    RecentEntryKind kind = System.IO.Directory.Exists(filePath) ? RecentEntryKind.Folder
                        : extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                          || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                          || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                            ? RecentEntryKind.Workspace : RecentEntryKind.File;
                    entry = new RecentFileEntry(filePath, kind);
                }
                else
                {
                    entry = element.Deserialize<RecentFileEntry>();
                }
                if (entry is null || string.IsNullOrWhiteSpace(entry.FilePath) || !Enum.IsDefined(entry.Kind)) continue;
                if (!FileSystemPathValidation.IsAccepted(entry.FilePath))
                {
                    _logger.LogWarning("Rejected recent entry with an invalid path: {Path}", entry.FilePath);
                    rejectedEntries++;
                    continue;
                }
                if (!seen.Add(entry.Kind + "\0" + entry.FilePath)) continue;
                RecentFiles.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to load recent files: {Message}", ex.Message);
        }
        finally
        {
            _isLoadingRecentFiles = false;
        }

        // Rewriting the list removes rejected entries from the stored history as well.
        if (rejectedEntries > 0)
            SaveRecentFiles();
    }

    private bool RejectInvalidPath(string path)
    {
        if (FileSystemPathValidation.IsAccepted(path))
            return false;

        _logger.LogWarning("Rejected invalid path: {Path}", path);
        SetPersistentStatus(string.Format(UiStrings.InvalidPathRejected, path));
        return true;
    }

    private void SaveRecentFiles()
    {
        if (_isLoadingRecentFiles || _recentFilesPath is null)
        {
            return;
        }

        try
        {
            string json = JsonSerializer.Serialize(RecentFiles, new JsonSerializerOptions { WriteIndented = true });
            WriteRecentFilesText(_recentFilesPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to save recent files: {Message}", ex.Message);
        }
    }

    private static string ReadRecentFilesText(string path)
    {
        const int attempts = 3;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(25);
            }
        }

        return SharedFileReader.ReadAllText(path);
    }

    private static void WriteRecentFilesText(string path, string content)
    {
        const int attempts = 3;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(25);
            }
        }

        System.IO.File.WriteAllText(path, content);
    }

    private int IndexOfRecentFile(string filePath, RecentEntryKind kind)
    {
        for (int i = 0; i < RecentFiles.Count; i++)
        {
            if (RecentFiles[i].Kind == kind && FileSystemPathComparison.Equals(RecentFiles[i].FilePath, filePath))
            {
                return i;
            }
        }

        return -1;
    }

    private void AddRecentFile(string filePath, RecentEntryKind kind = RecentEntryKind.File)
    {
        if (!FileSystemPathValidation.IsAccepted(filePath))
            return;

        int existingIndex = IndexOfRecentFile(filePath, kind);
        if (existingIndex >= 0)
        {
            RecentFiles.RemoveAt(existingIndex);
        }

        RecentFiles.Insert(0, new RecentFileEntry(filePath, kind));
    }

    private void RemoveRecentFile(string filePath, RecentEntryKind? kind = null)
    {
        for (int index = RecentFiles.Count - 1; index >= 0; index--)
        {
            RecentFileEntry entry = RecentFiles[index];
            if ((kind is null || entry.Kind == kind) && FileSystemPathComparison.Equals(entry.FilePath, filePath))
                RecentFiles.RemoveAt(index);
        }
    }

    private async System.Threading.Tasks.Task LoadWorkspaceAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        if (RejectInvalidPath(workspacePath))
            return;

        if (_workspaceService is null || _metadataService is null)
        {
            SetPersistentStatus(UiStrings.WorkspaceServicesUnavailable);
            return;
        }

        if (IsWorkspaceLoading || IsWorkspaceCommandRunning)
        {
            StatusText = UiStrings.WorkspaceBusy;
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        string extension = System.IO.Path.GetExtension(workspacePath);
        string workspaceName = System.IO.Path.GetFileName(workspacePath);
        SetPersistentStatus(string.Format(UiStrings.WorkspaceLoadingStatus, workspaceName));
        LogOutput("Info", $"Loading workspace: {workspacePath}");
        IsWorkspaceLoading = true;
        WorkspaceSession? session = _workspaceSessions.Load(workspacePath);
        bool loaded = false;

        try
        {
            WorkspaceModel workspace;
            try
            {
                workspace = extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                    ? await _workspaceService.LoadSolutionAsync(workspacePath, cancellationToken)
                    : await _workspaceService.LoadProjectAsync(workspacePath, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Workspace load failed: {Message}", ex.Message);
                LogOutput("Error", $"Workspace load failed: {ex.Message}");
                LogWorkspaceEnvironment(workspacePath);
                SetPersistentStatus(string.Format(UiStrings.WorkspaceLoadFailed, ex.Message));
                HasWorkspace = false;
                return;
            }

            StopDocumentPreviewers();
            // A workspace without build outputs loads no assemblies, so the resolver and design themes of the
            // previous one must not stay active.
            _assemblyResolver?.Dispose();
            _assemblyResolver = null;
            WorkspaceDesignThemeRegistry.Clear();
            _workspace = workspace;
            _controlCatalogUpdater?.SetWorkspaceAssemblies(Array.Empty<string>());
            _workspacePath = workspacePath;
            _workspaceInfoUpdater?.UpdateWorkspacePath(_workspacePath);
            HasWorkspace = true;
            RefreshWorkspaceProjects(workspace);

            string? name = System.IO.Path.GetFileNameWithoutExtension(workspacePath);
            SolutionExplorer.LoadWorkspace(workspace, name);
            SolutionExplorer.IsVisible = true;

            bool hasAnyProjectOutputs;
            bool hasMissingProjectOutputs;
            WorkspaceAssemblySet assemblySet = CollectWorkspaceAssemblies(
                workspace,
                out hasAnyProjectOutputs,
                out hasMissingProjectOutputs);
            if (!hasAnyProjectOutputs || hasMissingProjectOutputs)
            {
                await RunDotNetCommandAsync(workspacePath, "restore", cancellationToken: cancellationToken);
                await RunDotNetCommandAsync(workspacePath, "build", cancellationToken: cancellationToken);
                assemblySet = CollectWorkspaceAssemblies(
                    workspace,
                    out hasAnyProjectOutputs,
                    out hasMissingProjectOutputs);
            }

            LogAssemblySet(assemblySet, hasAnyProjectOutputs, hasMissingProjectOutputs);

            if (assemblySet.All.Count > 0)
            {
                SetPersistentStatus(string.Format(UiStrings.WorkspaceAssembliesLoading, workspaceName));
                ApplyAssemblyResolver(assemblySet);
                await Task.Run(() =>
                {
                    _metadataService.LoadAssemblies(assemblySet.All);
                }, cancellationToken);
                UpdateWorkspaceDesignThemes(workspace);
                RefreshOpenDocumentsAfterMetadataLoad();
            }

            foreach (DesignerDocumentViewModel document in Documents.OfType<DesignerDocumentViewModel>().ToArray())
            {
                if (document.UseExternalPreviewer)
                    await StartPreviewerForDocumentAsync(document);
                else
                    document.UseExternalPreviewer = true;
            }

            if (assemblySet.All.Count > 0) ShowExtensionView("toolbox.panel");
            RefreshControlCatalog();

            if (_languageRegistry is not null)
            {
                foreach (ILanguageIntellisenseService service in _languageRegistry.Services)
                {
                    await service.InitializeWorkspaceAsync(workspacePath);
                }
            }

            StatusText = string.Format(UiStrings.WorkspaceLoadedStatus, name);
            LogOutput("Info", $"Loaded workspace: {name}");
            StartLanguageWorkspaceWarmup();
            loaded = true;
        }
        finally
        {
            IsWorkspaceLoading = false;
        }

        if (loaded)
        {
            await RestoreWorkspaceSessionAsync(workspacePath, session);
        }
    }

    private void StartLanguageWorkspaceWarmup()
    {
        if (_languageRegistry is null || string.IsNullOrWhiteSpace(_workspacePath))
        {
            return;
        }

        string workspacePath = _workspacePath;
        foreach (ILanguageWorkspaceWarmup warmup in _languageRegistry.Services.OfType<ILanguageWorkspaceWarmup>())
        {
            _ = WarmLanguageWorkspaceAsync(warmup, workspacePath);
        }
    }

    private async Task WarmLanguageWorkspaceAsync(ILanguageWorkspaceWarmup warmup, string workspacePath)
    {
        using IDisposable? activity = _backgroundActivities?.Begin(
            string.Format(UiStrings.LanguageWorkspaceWarmupStatus, (object?)(System.IO.Path.GetFileName(workspacePath))));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!FileSystemPathComparison.Equals(_workspacePath, workspacePath)) return;
            await warmup.WarmWorkspaceAsync().ConfigureAwait(false);
            LogOutput("Info", $"Language workspace ready: {System.IO.Path.GetFileName(workspacePath)}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Language workspace warmup failed: {Message}", ex.Message);
        }
    }

    private void RefreshWorkspaceProjects(WorkspaceModel workspace)
    {
        WorkspaceProjects.Clear();
        _projectLookup.Clear();

        IReadOnlyList<ProjectModel> preferredProjects = ProjectSelection.DeduplicateProjectsByPath(workspace.Projects);
        foreach (ProjectModel project in preferredProjects)
        {
            WorkspaceProjects.Add(project);
            if (!string.IsNullOrWhiteSpace(project.ProjectPath))
            {
                _projectLookup[project.ProjectPath] = project;
            }
        }

        ProjectModel? selected = null;
        if (!string.IsNullOrWhiteSpace(ActiveProjectPath) &&
            _projectLookup.TryGetValue(ActiveProjectPath, out ProjectModel? existingByPath))
        {
            selected = existingByPath;
        }
        else if (ActiveProject is not null &&
            !string.IsNullOrWhiteSpace(ActiveProject.ProjectPath) &&
            _projectLookup.TryGetValue(ActiveProject.ProjectPath, out ProjectModel? existing))
        {
            selected = existing;
        }
        else if (ActiveDocument is not null)
        {
            selected = ProjectSelection.FindProjectForFile(workspace, ActiveDocument.FilePath);
        }
        else if (WorkspaceProjects.Count > 0)
        {
            selected = ProjectSelection.SelectPreferredProject(WorkspaceProjects);
        }

        SetActiveProject(selected);
    }

    private async System.Threading.Tasks.Task TryLoadWorkspaceForXamlAsync(string xamlFilePath)
    {
        // Never swap an already loaded workspace: opening a XAML file that lives outside the
        // workspace (generated output, scratch folders) must keep the current workspace so the
        // designer resolves its types; the file is opened as a plain document instead.
        if (_workspace is not null)
        {
            return;
        }

        string? workspacePath = FindWorkspacePathForFile(xamlFilePath);
        if (string.IsNullOrEmpty(workspacePath))
        {
            return;
        }

        if (FileSystemPathComparison.Equals(_workspacePath, workspacePath))
        {
            return;
        }

        await LoadWorkspaceAsync(workspacePath);
    }

    private static bool WorkspaceContainsFile(WorkspaceModel workspace, string xamlFilePath)
    {
        foreach (ProjectModel project in workspace.Projects)
        {
            foreach (XamlFileModel file in project.XamlFiles)
            {
                if (FileSystemPathComparison.Equals(file.FilePath, xamlFilePath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsXamlFile(string filePath)
    {
        return filePath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
    }

    private string? FindWorkspacePathForFile(string filePath)
    {
        string? projectSourceRoot = System.Environment.GetEnvironmentVariable("XVE_PROJECT_SOURCE_ROOT");
        string? generatedProjectPath = GetGeneratedProjectCandidate(
            filePath,
            projectSourceRoot,
            System.IO.File.Exists);
        if (!string.IsNullOrEmpty(generatedProjectPath))
        {
            return generatedProjectPath;
        }

        string? currentDir = System.IO.Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(currentDir))
        {
            string? solutionPath = GetFirstFile(currentDir, "*.sln");
            if (string.IsNullOrEmpty(solutionPath))
            {
                solutionPath = GetFirstFile(currentDir, "*.slnx");
            }
            if (!string.IsNullOrEmpty(solutionPath))
            {
                return solutionPath;
            }

            string? projectPath = GetFirstFile(currentDir, "*.csproj");
            if (!string.IsNullOrEmpty(projectPath))
            {
                return projectPath;
            }

            currentDir = System.IO.Path.GetDirectoryName(currentDir);
        }

        return null;
    }

    internal static string? GetGeneratedProjectCandidate(
        string filePath,
        string? projectSourceRoot,
        Func<string, bool> projectFileExists)
    {
        if (string.IsNullOrWhiteSpace(projectSourceRoot))
        {
            return null;
        }

        string fullPath;
        string fullProjectSourceRoot;
        try
        {
            fullPath = System.IO.Path.GetFullPath(filePath);
            fullProjectSourceRoot = System.IO.Path.GetFullPath(projectSourceRoot);
        }
        catch (Exception)
        {
            return null;
        }

        System.IO.DirectoryInfo? currentDirectory = new System.IO.FileInfo(fullPath).Directory;
        while (currentDirectory is not null)
        {
            string projectName = currentDirectory.Name;
            string candidate = System.IO.Path.Combine(
                fullProjectSourceRoot,
                projectName,
                projectName + ".csproj");
            if (projectFileExists(candidate))
            {
                return candidate;
            }

            currentDirectory = currentDirectory.Parent;
        }

        return null;
    }

    private string? GetFirstFile(string directory, string pattern)
    {
        try
        {
            foreach (string file in System.IO.Directory.EnumerateFiles(directory, pattern))
            {
                return file;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to enumerate '{Directory}': {Message}", directory, ex.Message);
        }

        return null;
    }

    private async System.Threading.Tasks.Task<bool> RunDotNetCommandAsync(
        string workspacePath,
        string command,
        string? option = null,
        CancellationToken cancellationToken = default)
    {
        string? workingDirectory = System.IO.Path.GetDirectoryName(workspacePath);
        if (string.IsNullOrEmpty(workingDirectory))
        {
            return false;
        }

        List<string> arguments = new(3)
        {
            command,
            workspacePath
        };
        if (!string.IsNullOrWhiteSpace(option))
        {
            arguments.Add(option);
        }

        string displayCommand = string.IsNullOrWhiteSpace(option) ? command : $"{command} {option}";
        LogOutput("Info", $"dotnet {displayCommand} {workspacePath}");
        DotNetCliResult result = await _dotNetCli.RunAsync(arguments, workingDirectory, cancellationToken);

        if (!result.Success)
        {
            string error = !string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardError
                : result.StandardOutput;
            _logger.LogWarning("dotnet {Command} failed: {Error}", displayCommand, error);
            SetPersistentStatus(string.Format(UiStrings.DotnetCommandFailed, displayCommand));
            if (!string.IsNullOrWhiteSpace(error))
            {
                LogOutput("Error", error.Trim());
            }

            return false;
        }

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger.LogInformation("{Output}", result.StandardOutput);
            LogOutput("Info", result.StandardOutput.Trim());
        }

        return true;
    }

    private void LogOutput(string level, string message)
    {
        if (_outputChannel is not null)
        {
            _outputChannel.AppendLine($"[{level}] {message}");
        }

        switch (level)
        {
            case "Error":
                _logger.LogError(message);
                break;
            case "Warning":
                _logger.LogWarning(message);
                break;
            case "Debug":
                _logger.LogDebug(message);
                break;
            case "Trace":
                _logger.LogTrace(message);
                break;
            default:
                _logger.LogInformation(message);
                break;
        }
    }

    private System.Threading.Tasks.Task<bool> ConfirmDebugToolConsentAsync(DebugToolConsentRequest request)
    {
        return DebugToolConsentInteraction.Handle(request);
    }

    private void ConfigurePreviewThemes(DesignerDocumentViewModel? document) =>
        document?.SetPreviewThemeProviders(_extensionContributions?.DesignerThemeProviders
            ?? Array.Empty<DesignerThemeProviderRegistration>());

    private async System.Threading.Tasks.Task UpdatePreviewerForDocumentAsync(DesignerDocumentViewModel document)
    {
        WorkspaceModel? workspace = _workspace;
        string? workspacePath = _workspacePath;
        if (workspace is null || document.IsDisposed || !Documents.Contains(document)
            || !_previewerLaunchService.TryGetSession(document.FilePath, out _)) return;
        string? xaml = GetPreviewerXaml(document);
        if (string.IsNullOrWhiteSpace(xaml)) return;
        try
        {
            PreviewerLaunchResult result = await _previewerLaunchService.SendUpdateXamlAsync(document.FilePath, xaml, workspace,
                (level, message) => LogOutput(level, message), workspacePath, document.PreviewThemeRequest);
            if (!document.IsDisposed && ReferenceEquals(workspace, _workspace))
                ApplyPreviewerUpdateResult(document, result);
        }
        catch (Exception) when (document.IsDisposed || !Documents.Contains(document) || !ReferenceEquals(workspace, _workspace))
        {
            // Closing a document or replacing its workspace invalidates pending transport work.
        }
    }

    // A theme change restarts the host with a new session. A failed update keeps a host that still runs; only a host
    // that failed to restart leaves the document without a session.
    internal void ApplyPreviewerUpdateResult(DesignerDocumentViewModel document, PreviewerLaunchResult result)
    {
        bool hasSession = _previewerLaunchService.TryGetSession(document.FilePath, out PreviewerTcpSession? session);
        if (hasSession)
        {
            if (!ReferenceEquals(session, document.PreviewerSession)) document.PreviewerSession = session;
            document.PreviewerState = PreviewerState.Running;
        }
        if (result.Success)
        {
            document.SetPreviewerLaunchError(null);
            return;
        }

        document.SetPreviewerLaunchError(result.ErrorMessage ?? UiStrings.PreviewerStartFailedText);
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage)) LogOutput("Error", result.ErrorMessage);
        if (hasSession) return;
        document.PreviewerSession = null;
        document.PreviewerState = PreviewerState.Failed;
        if (ReferenceEquals(ActiveDesignerDocument, document)) SetPersistentStatus(UiStrings.PreviewerStartFailed);
    }

    private static string? GetPreviewerXaml(DesignerDocumentViewModel document)
    {
        if (document.MetadataService is { } metadata && document.SyncEngine.CanEditAst
            && document.SyncEngine.CurrentDocument is { Root: not null } ast)
            return new XamlVisualEditor.Designer.Rendering.RuntimeDesignRenderer(metadata)
                .PrepareXaml(ast, themeVariant: document.DesignSurface.PreviewThemeVariant, themeRequest: document.PreviewThemeRequest);
        return document.SyncEngine.CurrentText;
    }

    // Stopping a host the editor started is no exit; a previewer that had already exited or failed stays reported so.
    private static void MarkPreviewerStopped(DesignerDocumentViewModel document)
    {
        if (document.PreviewerState is PreviewerState.Running or PreviewerState.Starting)
            document.PreviewerState = PreviewerState.Stopped;
    }

    private bool IsWithinLivePreviewers(DesignerDocumentViewModel document)
    {
        int index = _previewerActivationOrder.IndexOf(document);
        return index >= 0 && index < MaxLivePreviewers;
    }

    // Every previewer is a separate host process; only the recently active documents keep theirs.
    private void TrackPreviewerActivation(DesignerDocumentViewModel document)
    {
        _previewerActivationOrder.Remove(document);
        _previewerActivationOrder.Insert(0, document);
        _previewerActivationOrder.RemoveAll(item => item.IsDisposed || !Documents.Contains(item));
        foreach (DesignerDocumentViewModel inactive in _previewerActivationOrder.Skip(MaxLivePreviewers))
        {
            inactive.PreviewerSession = null;
            _previewerLaunchService.StopPreviewer(inactive.FilePath);
            MarkPreviewerStopped(inactive);
        }

        // A canceled start stays canceled when the document becomes active again; the previewer toggle starts it again.
        if (document.UseExternalPreviewer && document.IsLoaded && document.PreviewerSession is null
            && document.PreviewerState != PreviewerState.Canceled
            && document.StartPreviewerCommand is { } start && start.CanExecute(null))
        {
            start.Execute(null);
        }
    }

    private System.Threading.Tasks.Task StartPreviewerForActiveDocumentAsync() =>
        ActiveDesignerDocument is { } document
            ? StartPreviewerForDocumentAsync(document)
            : System.Threading.Tasks.Task.CompletedTask;

    private async System.Threading.Tasks.Task StartPreviewerForDocumentAsync(DesignerDocumentViewModel document)
    {
        WorkspaceModel? workspace = _workspace;
        string? workspacePath = _workspacePath;
        if (document.IsDisposed || !document.IsLoaded || !Documents.Contains(document)) return;
        if (_changingThemePackages.Any(id => UsesThemePackage(document, id))) return;
        // A document outside the live set starts its previewer when it becomes active again.
        if (!IsWithinLivePreviewers(document)) return;
        ConfigurePreviewThemes(document);
        document.SetPreviewerLaunchError(null);
        if (workspace is null)
        {
            document.SetPreviewerLaunchError(UiStrings.PreviewerNeedsProject);
            document.PreviewerState = PreviewerState.Failed;
            return;
        }

        document.PreviewerState = PreviewerState.Starting;
        if (!await EnsurePreviewerTrustAsync(document.FilePath))
        {
            if (ReferenceEquals(ActiveDesignerDocument, document)) StatusText = UiStrings.PreviewerStartCanceled;
            document.PreviewerState = PreviewerState.Canceled;
            return;
        }

        if (document.IsDisposed || !Documents.Contains(document) || !ReferenceEquals(_workspace, workspace))
        {
            MarkPreviewerStopped(document);
            return;
        }
        string? xamlText = GetPreviewerXaml(document);
        PreviewerLaunchResult result = await _previewerLaunchService.StartPreviewerAsync(
            document.FilePath,
            xamlText,
            workspace,
            workspacePath,
            command => ReferenceEquals(_workspace, workspace)
                ? RunWorkspaceCommandAsync(command) : System.Threading.Tasks.Task.CompletedTask,
            (level, message) => LogOutput(level, message),
            document.PreviewThemeRequest);

        if (document.IsDisposed || !Documents.Contains(document) || !ReferenceEquals(_workspace, workspace))
        {
            if (result.Process is not null) _previewerLaunchService.StopPreviewer(document.FilePath, result.Process);
            MarkPreviewerStopped(document);
            return;
        }

        if (result.Success)
        {
            if (ReferenceEquals(ActiveDesignerDocument, document)) StatusText = UiStrings.PreviewerStarted;
            if (_previewerLaunchService.TryGetSession(document.FilePath, out PreviewerTcpSession? session))
            {
                document.PreviewerSession = session;
                document.PreviewerState = PreviewerState.Running;
            }
            else if (document.PreviewerState == PreviewerState.Starting)
            {
                // The host was released after the start returned and the editor did not stop it, so its process
                // exited; OnPreviewerExited skips a document that is still starting.
                document.PreviewerState = PreviewerState.Exited;
            }
            return;
        }

        if (ReferenceEquals(ActiveDesignerDocument, document)) SetPersistentStatus(UiStrings.PreviewerStartFailed);
        document.PreviewerSession = null;
        document.PreviewerState = PreviewerState.Failed;
        document.SetPreviewerLaunchError(result.ErrorMessage ?? UiStrings.PreviewerStartFailedText);
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            LogOutput("Error", result.ErrorMessage);
        }
    }

    private async System.Threading.Tasks.Task<bool> EnsurePreviewerTrustAsync(string xamlFilePath)
    {
        // Headless bridge runs cannot answer the trust dialog; the switch grants the start,
        // like XVE_IDEBRIDGE_AUTOCONSENT does for the bridge.
        string? autoTrust = System.Environment.GetEnvironmentVariable("XVE_PREVIEWER_AUTOTRUST");
        if (string.Equals(autoTrust, "1", StringComparison.Ordinal)
            || string.Equals(autoTrust, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string root = GetPreviewerTrustRoot(xamlFilePath);
        if (_trustedPreviewerRoots.Contains(root))
        {
            return true;
        }

        PreviewerTrustRequest request = new(
            UiStrings.StartPreviewer,
            UiStrings.PreviewerTrustExplanation,
            root);

        PreviewerTrustDecision decision = await PreviewerTrustInteraction.Handle(request);
        if (decision == PreviewerTrustDecision.TrustWorkspace)
        {
            _trustedPreviewerRoots.Add(root);
            SaveTrustedPreviewerRoots();
            return true;
        }

        return decision == PreviewerTrustDecision.AllowOnce;
    }

    /// <summary>
    /// Starts the previewer for an open XAML file, waits for a stable frame and saves it as
    /// PNG. A previewer process started for the export ends afterwards, so batch runs do not accumulate host
    /// processes; the live previewer of a document keeps running.
    /// </summary>
    public async System.Threading.Tasks.Task<XamlVisualEditor.Extensions.PreviewExportResult> ExportPreviewAsync(
        string filePath,
        string outputPath,
        int timeoutMs)
    {
        string requestedPath = NormalizeAcceptedDocumentPath(filePath);
        DesignerDocumentViewModel? doc = Documents
            .OfType<DesignerDocumentViewModel>()
            .FirstOrDefault(d => FileSystemPathComparison.Equals(d.FilePath, requestedPath));
        if (doc is null)
        {
            return new(false, UiStrings.DesignerDocumentNotOpen, 0, 0, outputPath);
        }

        // The previewer service keys its hosts by the path of the open document.
        filePath = doc.FilePath;
        if (_workspace is null)
        {
            return new(false, UiStrings.NoWorkspaceLoaded, 0, 0, outputPath);
        }

        if (!await EnsurePreviewerTrustAsync(filePath))
        {
            return new(false, UiStrings.PreviewerTrustDenied, 0, 0, outputPath);
        }

        ActiveDocument = doc;
        // A document that shows its live previewer keeps it after the export; only a host started for the export ends.
        bool hasSession = _previewerLaunchService.TryGetSession(filePath, out PreviewerTcpSession? liveSession);
        bool usesLivePreviewer = doc.UseExternalPreviewer || hasSession;
        // A running previewer still shows the frame of an earlier state until it renders the exported one.
        Avalonia.Remote.Protocol.Viewport.FrameMessage? staleFrame = liveSession?.LastFrame;
        PreviewerLaunchResult launch = await _previewerLaunchService.StartPreviewerAsync(
            filePath,
            GetPreviewerXaml(doc),
            _workspace,
            _workspacePath,
            command => RunWorkspaceCommandAsync(command),
            (level, message) => LogOutput(level, message),
            doc.PreviewThemeRequest,
            // An unchanged document gives no new frame on an incremental update; the full render does.
            fullRender: true);
        if (!launch.Success)
        {
            return new(false, launch.ErrorMessage ?? UiStrings.PreviewerStartFailedText, 0, 0, outputPath);
        }

        if (!_previewerLaunchService.TryGetSession(filePath, out PreviewerTcpSession? session) || session is null)
        {
            return new(false, UiStrings.NoPreviewerSession, 0, 0, outputPath);
        }

        doc.PreviewerSession = session;
        doc.PreviewerState = PreviewerState.Running;
        try
        {
            Avalonia.Remote.Protocol.Viewport.FrameMessage? frame = await WaitForStableFrameAsync(session, timeoutMs, staleFrame);
            if (frame is null)
            {
                return new(false, UiStrings.PreviewerFrameTimeout, 0, 0, outputPath);
            }

            if (frame.Format != Avalonia.Remote.Protocol.Viewport.PixelFormat.Bgra8888)
            {
                return new(false, $"Unsupported frame format {frame.Format}.", 0, 0, outputPath);
            }

            SaveFrameAsPng(frame, outputPath);
            return new(true, null, frame.Width, frame.Height, outputPath);
        }
        finally
        {
            if (!usesLivePreviewer)
            {
                doc.PreviewerSession = null;
                _previewerLaunchService.StopPreviewer(filePath, launch.Process);
                MarkPreviewerStopped(doc);
            }
        }
    }

    // Frames arrive several times while the preview builds up (viewport negotiation); the last
    // frame counts as stable once 1.5 s passed without a size change or resize request. The stale frame, which the
    // session held before the update, and frames without size never count.
    internal static async System.Threading.Tasks.Task<Avalonia.Remote.Protocol.Viewport.FrameMessage?> WaitForStableFrameAsync(
        PreviewerTcpSession session,
        int timeoutMs,
        Avalonia.Remote.Protocol.Viewport.FrameMessage? staleFrame = null)
    {
        static bool IsUsable(Avalonia.Remote.Protocol.Viewport.FrameMessage? frame,
            Avalonia.Remote.Protocol.Viewport.FrameMessage? stale) =>
            frame is { Width: > 0, Height: > 0 } && !ReferenceEquals(frame, stale);

        object gate = new();
        Avalonia.Remote.Protocol.Viewport.FrameMessage? last = null;
        DateTime lastChangeUtc = DateTime.UtcNow;

        void OnFrame(Avalonia.Remote.Protocol.Viewport.FrameMessage frame)
        {
            if (!IsUsable(frame, staleFrame)) return;
            lock (gate)
            {
                if (last is null || frame.Width != last.Width || frame.Height != last.Height)
                {
                    lastChangeUtc = DateTime.UtcNow;
                }

                last = frame;
            }
        }

        void OnResize(Avalonia.Remote.Protocol.Viewport.RequestViewportResizeMessage resize)
        {
            session.UpdateViewport(Math.Clamp(resize.Width, 1, 4096), Math.Clamp(resize.Height, 1, 4096), 96, 96);
            lock (gate)
            {
                lastChangeUtc = DateTime.UtcNow;
            }
        }

        session.FrameReceived += OnFrame;
        session.ViewportResizeRequested += OnResize;
        try
        {
            // Read after subscribing, so a frame that arrived before the subscription is not lost.
            Avalonia.Remote.Protocol.Viewport.FrameMessage? current = session.LastFrame;
            lock (gate)
            {
                if (last is null && IsUsable(current, staleFrame)) last = current;
            }

            DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadlineUtc)
            {
                await System.Threading.Tasks.Task.Delay(250);
                lock (gate)
                {
                    if (last is not null && (DateTime.UtcNow - lastChangeUtc).TotalMilliseconds > 1500)
                    {
                        return last;
                    }
                }
            }

            lock (gate)
            {
                return last;
            }
        }
        finally
        {
            session.FrameReceived -= OnFrame;
            session.ViewportResizeRequested -= OnResize;
        }
    }

    private static void SaveFrameAsPng(Avalonia.Remote.Protocol.Viewport.FrameMessage frame, string outputPath)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using Avalonia.Media.Imaging.WriteableBitmap bitmap = new(
            new Avalonia.PixelSize(frame.Width, frame.Height),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
        using (Avalonia.Platform.ILockedFramebuffer buffer = bitmap.Lock())
        {
            int stride = frame.Stride > 0 ? frame.Stride : frame.Width * 4;
            int lineLength = Math.Min(frame.Width * 4, buffer.RowBytes);
            for (int y = 0; y < frame.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    frame.Data,
                    y * stride,
                    buffer.Address + y * buffer.RowBytes,
                    lineLength);
            }
        }

#pragma warning disable CS0618 // Save(string) schreibt PNG; die BitmapEncoderOptions-Ueberladung bietet hier keinen Mehrwert
        bitmap.Save(outputPath);
#pragma warning restore CS0618
    }

    private string GetPreviewerTrustRoot(string xamlFilePath)
    {
        if (!string.IsNullOrWhiteSpace(_workspacePath))
        {
            return _workspacePath;
        }

        string? dir = Path.GetDirectoryName(xamlFilePath);
        return string.IsNullOrWhiteSpace(dir) ? xamlFilePath : dir;
    }

    private void LoadTrustedPreviewerRoots()
    {
        try
        {
            string path = GetTrustedPreviewerRootsPath();
            if (!File.Exists(path))
            {
                return;
            }

            string json = SharedFileReader.ReadAllText(path);
            List<string>? roots = JsonSerializer.Deserialize<List<string>>(json);
            if (roots is null)
            {
                return;
            }

            foreach (string root in roots)
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    _trustedPreviewerRoots.Add(root);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to load previewer trust list: {Message}", ex.Message);
        }
    }

    private void SaveTrustedPreviewerRoots()
    {
        try
        {
            string path = GetTrustedPreviewerRootsPath();
            List<string> roots = _trustedPreviewerRoots.ToList();
            string json = JsonSerializer.Serialize(roots, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to save previewer trust list: {Message}", ex.Message);
        }
    }

    private static string GetTrustedPreviewerRootsPath()
    {
        return Path.Combine(ShellSettingsPaths.GetRoot(), "previewer-trust.json");
    }

    private void LogDiagnosticsSummary(IReadOnlyList<XamlDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return;
        }

        int errorCount = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warningCount = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        if (errorCount == 0 && warningCount == 0)
        {
            return;
        }

        LogOutput("Info", $"XAML diagnostics: {errorCount} error(s), {warningCount} warning(s)");
    }

    internal void OnPreviewerErrorReceived(PreviewerErrorInfo error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (string.IsNullOrWhiteSpace(error.FilePath))
            {
                LogOutput("Error", error.Message);
                return;
            }

            // The previewer reports positions in its preview copy; the output names the position in the source.
            DesignerDocumentViewModel? document = Documents.OfType<DesignerDocumentViewModel>()
                .FirstOrDefault(candidate => FileSystemPathComparison.Equals(candidate.FilePath, error.FilePath));
            (int line, int column) = DesignerDocumentViewModel.ResolvePreviewerErrorPosition(error, document?.SyncEngine.CurrentText);
            Output.AddDiagnostic(new XamlDiagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = error.Message,
                Line = line,
                Column = column,
                Length = 1
            }, error.FilePath);
        }, DispatcherPriority.Background);
    }

    // The exit arrives on a process thread. A start that began in the meantime reports its own result, and a host
    // that already took over keeps the document running.
    internal void OnPreviewerExited(string xamlFilePath)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_previewerLaunchService.TryGetSession(xamlFilePath, out _)) return;
            foreach (DesignerDocumentViewModel document in Documents.OfType<DesignerDocumentViewModel>()
                .Where(candidate => !candidate.IsDisposed && candidate.PreviewerState != PreviewerState.Starting
                    && FileSystemPathComparison.Equals(candidate.FilePath, xamlFilePath)).ToList())
                document.PreviewerState = PreviewerState.Exited;
        }, DispatcherPriority.Background);
    }

    Task IWorkspaceCommands.LoadWorkspaceAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return System.Threading.Tasks.Task.FromCanceled(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(_workspacePath))
        {
            StatusText = UiStrings.NoWorkspaceLoadedStatus;
            return Task.CompletedTask;
        }

        if (System.IO.Directory.Exists(_workspacePath))
        {
            StatusText = UiStrings.ReloadWorkspaceFileRequired;
            return Task.CompletedTask;
        }

        if (!System.IO.File.Exists(_workspacePath))
        {
            SetPersistentStatus(string.Format(UiStrings.WorkspaceFileNotFound, (object?)(System.IO.Path.GetFileName(_workspacePath))));
            return Task.CompletedTask;
        }

        return LoadWorkspaceAsync(_workspacePath, cancellationToken);
    }

    Task IWorkspaceCommands.RestoreWorkspaceAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return System.Threading.Tasks.Task.FromCanceled(cancellationToken);
        }

        return RunWorkspaceCommandAsync("restore", cancellationToken: cancellationToken);
    }

    Task IWorkspaceCommands.BuildWorkspaceAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return System.Threading.Tasks.Task.FromCanceled(cancellationToken);
        }

        return RunWorkspaceCommandAsync("build", cancellationToken: cancellationToken);
    }

    Task IWorkspaceCommands.RebuildWorkspaceAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return System.Threading.Tasks.Task.FromCanceled(cancellationToken);
        }

        return RunWorkspaceCommandAsync("build", "-t:Rebuild", cancellationToken);
    }

    Task IWorkspaceCommands.CleanWorkspaceAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return System.Threading.Tasks.Task.FromCanceled(cancellationToken);
        }

        return RunWorkspaceCommandAsync("clean", cancellationToken: cancellationToken);
    }

    Task IWorkspaceCommands.SetStartupProjectAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return System.Threading.Tasks.Task.FromCanceled(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            SetActiveProject(null);
            return Task.CompletedTask;
        }

        ProjectModel? project = ResolveProjectByPath(projectPath, targetFramework);
        if (project is not null)
        {
            SetActiveProject(project);
            return Task.CompletedTask;
        }

        SetActiveProjectByPath(projectPath);
        return Task.CompletedTask;
    }

    Task IWorkspaceCommands.UndoAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(UndoCommand, cancellationToken);

    Task IWorkspaceCommands.RedoAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(RedoCommand, cancellationToken);

    Task IWorkspaceCommands.CutAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(CutCommand, cancellationToken);

    Task IWorkspaceCommands.CopyAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(CopyCommand, cancellationToken);

    Task IWorkspaceCommands.PasteAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(PasteCommand, cancellationToken);

    Task IWorkspaceCommands.DeleteAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(DeleteCommand, cancellationToken);

    Task IWorkspaceCommands.SelectAllAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(SelectAllCommand, cancellationToken);

    Task IWorkspaceCommands.RenameSymbolAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(RenameSymbolCommand, cancellationToken);

    Task IWorkspaceCommands.FormatDocumentAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(FormatDocumentCommand, cancellationToken);

    Task IWorkspaceCommands.ShowCodeActionsAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(CodeActionsCommand, cancellationToken);

    Task IWorkspaceCommands.ShowDocumentSymbolsAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(DocumentSymbolsCommand, cancellationToken);

    Task IWorkspaceCommands.ShowWorkspaceSymbolsAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(WorkspaceSymbolsCommand, cancellationToken);

    Task IWorkspaceCommands.ToggleBreakpointsAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(ToggleBreakpointsCommand, cancellationToken);

    Task IWorkspaceCommands.ToggleCallStackAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(ToggleCallStackCommand, cancellationToken);

    Task IWorkspaceCommands.ToggleLocalsAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(ToggleLocalsCommand, cancellationToken);

    Task IWorkspaceCommands.ToggleWatchesAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(ToggleWatchesCommand, cancellationToken);

    Task IWorkspaceCommands.StartDebugAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(StartDebugCommand, cancellationToken);

    Task IWorkspaceCommands.StopDebugAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(StopDebugCommand, cancellationToken);

    Task IWorkspaceCommands.ContinueDebugAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(ContinueDebugCommand, cancellationToken);

    Task IWorkspaceCommands.StepOverAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(StepOverCommand, cancellationToken);

    Task IWorkspaceCommands.StepInAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(StepInCommand, cancellationToken);

    Task IWorkspaceCommands.StepOutAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(StepOutCommand, cancellationToken);

    Task IWorkspaceCommands.PauseDebugAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(PauseDebugCommand, cancellationToken);

    Task IWorkspaceCommands.ToggleBreakpointAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(ToggleBreakpointCommand, cancellationToken);

    Task IWorkspaceCommands.StartRunAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(StartRunCommand, cancellationToken);

    Task IWorkspaceCommands.StopRunAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(StopRunCommand, cancellationToken);

    Task IWorkspaceCommands.NewTerminalAsync(CancellationToken cancellationToken) =>
        ExecuteReactiveCommandAsync(NewTerminalCommand, cancellationToken);

    private async System.Threading.Tasks.Task RunWorkspaceCommandAsync(
        string command,
        string? option = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_workspacePath) || _workspace is null)
        {
            return;
        }

        if (IsWorkspaceLoading || IsWorkspaceCommandRunning)
        {
            StatusText = UiStrings.WorkspaceBusy;
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        string displayCommand = string.IsNullOrWhiteSpace(option) ? command : $"{command} {option}";
        SetPersistentStatus(string.Format(UiStrings.DotnetCommandRunning, displayCommand));
        IsWorkspaceCommandRunning = true;

        try
        {
            bool success = await RunDotNetCommandAsync(
                _workspacePath,
                command,
                option,
                cancellationToken);
            if (!success || _metadataService is null)
            {
                return;
            }

            bool hasAnyProjectOutputs;
            bool hasMissingProjectOutputs;
            WorkspaceAssemblySet assemblySet = CollectWorkspaceAssemblies(
                _workspace,
                out hasAnyProjectOutputs,
                out hasMissingProjectOutputs);

            if (assemblySet.All.Count > 0)
            {
                ApplyAssemblyResolver(assemblySet);
                await Task.Run(() =>
                {
                    _metadataService.LoadAssemblies(assemblySet.All);
                }, cancellationToken);
                UpdateWorkspaceDesignThemes(_workspace);
                RefreshOpenDocumentsAfterMetadataLoad();
            }
            RefreshControlCatalog();
            LogAssemblySet(assemblySet, hasAnyProjectOutputs, hasMissingProjectOutputs);
            StatusText = string.Format(UiStrings.DotnetCommandCompleted, displayCommand);
        }
        finally
        {
            IsWorkspaceCommandRunning = false;
        }
    }

    // Feeds the design-surface theme registry from the workspace application's
    // App.axaml so instantiated controls get the target app's themes and resources.
    private void UpdateWorkspaceDesignThemes(WorkspaceModel workspace)
    {
        // Library workspaces have no application of their own; the override names the
        // App.axaml whose themes the design surface should apply in that case.
        string? overridePath = WorkspaceDesignThemeLoader.GetApplicationXamlOverridePath();
        string? appXamlPath = overridePath ?? FindApplicationXamlPath(workspace);
        if (appXamlPath is null)
        {
            WorkspaceDesignThemeRegistry.Clear();
            LogOutput("Info", string.Format(UiStrings.DesignThemesNoAppXaml, WorkspaceDesignThemeLoader.ApplicationXamlOverrideVariable));
            return;
        }

        try
        {
            IReadOnlyList<string> details = WorkspaceDesignThemeLoader.LoadFromApplicationXaml(
                appXamlPath, message => LogOutput("Warning", message));
            string sourceLabel = overridePath is null
                ? appXamlPath
                : $"{appXamlPath} ({WorkspaceDesignThemeLoader.ApplicationXamlOverrideVariable})";
            LogOutput("Info", string.Format(UiStrings.DesignThemesLoaded, sourceLabel, string.Join("; ", details)));
        }
        catch (Exception ex)
        {
            WorkspaceDesignThemeRegistry.Clear();
            LogOutput("Warning", string.Format(UiStrings.DesignThemesFailed, appXamlPath, ex.Message));
        }
    }

    private static string? FindApplicationXamlPath(WorkspaceModel workspace)
    {
        foreach (ProjectModel project in workspace.Projects.OrderByDescending(p => p.IsExecutable))
        {
            foreach (XamlFileModel file in project.XamlFiles)
            {
                string fileName = System.IO.Path.GetFileName(file.FilePath);
                if (fileName.Equals("App.axaml", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("App.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    return file.FilePath;
                }
            }
        }

        foreach (ProjectModel project in workspace.Projects.OrderByDescending(p => p.IsExecutable))
        {
            string? projectDir = System.IO.Path.GetDirectoryName(project.ProjectPath);
            if (string.IsNullOrEmpty(projectDir))
            {
                continue;
            }

            foreach (string candidate in new[] { "App.axaml", "App.xaml" })
            {
                string path = System.IO.Path.Combine(projectDir, candidate);
                if (System.IO.File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private void StopDocumentPreviewers()
    {
        foreach (DesignerDocumentViewModel document in Documents.OfType<DesignerDocumentViewModel>())
        {
            document.PreviewerSession = null;
            _previewerLaunchService.StopPreviewer(document.FilePath);
            MarkPreviewerStopped(document);
        }
    }

    private void RefreshOpenDocumentsAfterMetadataLoad()
    {
        foreach (IEditorDocumentViewModel doc in Documents)
        {
            if (doc is DesignerDocumentViewModel designer)
            {
                designer.DesignSurface.RequestRebuild();
            }
        }
    }

    private void RefreshControlCatalog()
    {
        if (_controlCatalogUpdater is null || _workspace is null) return;
        ProjectModel? project = ActiveDesignerDocument?.FilePath is { } filePath
            ? ProjectSelection.FindProjectForFile(_workspace, filePath) : null;
        if (project is not null && ActiveProject is { } active
            && FileSystemPathComparison.Equals(project.ProjectPath, active.ProjectPath))
            project = active;
        project ??= ActiveProject ?? ProjectSelection.SelectPreferredProject(_workspace.Projects);
        _controlCatalogUpdater.SetWorkspaceAssemblies(
            project is null ? Array.Empty<string>() : WorkspaceAssemblyDiscovery.FindCatalogAssemblies(project));
    }

    private WorkspaceAssemblySet CollectWorkspaceAssemblies(
        WorkspaceModel workspace,
        out bool hasAnyProjectOutputs,
        out bool hasMissingProjectOutputs)
    {
        List<string> all = new();
        List<string> preferred = new();
        HashSet<string> seen = new(FileSystemPathComparison.Comparer);
        hasAnyProjectOutputs = false;
        hasMissingProjectOutputs = false;

        foreach (ProjectModel project in workspace.Projects)
        {
            IReadOnlyList<string> outputs = WorkspaceAssemblyDiscovery.FindProjectOutputs(
                project,
                (root, ex) => _logger.LogWarning(
                    "Failed to enumerate outputs from '{Root}': {Message}",
                    root,
                    ex.Message));
            if (outputs.Count == 0)
            {
                hasMissingProjectOutputs = true;
            }

            foreach (string outputPath in outputs)
            {
                if (!IsAssemblyPathCandidate(outputPath))
                {
                    continue;
                }

                if (System.IO.File.Exists(outputPath) && seen.Add(outputPath))
                {
                    all.Add(outputPath);
                    preferred.Add(outputPath);
                    hasAnyProjectOutputs = true;
                }
            }

            // The built output directory is the runtime source of truth. Project references are only a
            // fallback when no output is available; mixing both sources loads duplicate package assemblies.
            if (outputs.Count > 0)
            {
                continue;
            }

            foreach (AssemblyReference reference in project.References)
            {
                if (!IsAssemblyPathCandidate(reference.Path))
                {
                    continue;
                }

                if (System.IO.File.Exists(reference.Path) && seen.Add(reference.Path))
                {
                    all.Add(reference.Path);
                }
            }
        }

        return new WorkspaceAssemblySet(all, preferred);
    }

    private static bool IsAssemblyPathCandidate(string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            return false;
        }

        string extension = System.IO.Path.GetExtension(assemblyPath);
        if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (System.IO.Path.GetFileName(assemblyPath)
            .EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsReferenceAssemblyPath(assemblyPath);
    }

    private static bool IsReferenceAssemblyPath(string assemblyPath)
    {
        string normalized = assemblyPath.Replace(System.IO.Path.AltDirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar);
        string marker = System.IO.Path.DirectorySeparatorChar.ToString();
        return normalized.Contains(marker + "ref" + marker, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(marker + "refint" + marker, StringComparison.OrdinalIgnoreCase);
    }

    private void LogWorkspaceEnvironment(string workspacePath)
    {
        try
        {
            _logger.LogInformation("Workspace path: {Path}", workspacePath);
            _logger.LogInformation("Working directory: {WorkingDirectory}", System.IO.Directory.GetCurrentDirectory());
            _logger.LogInformation("DOTNET_ROOT: {DotnetRoot}", Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty);
            _logger.LogInformation("PATH: {Path}", Environment.GetEnvironmentVariable("PATH") ?? string.Empty);

            string info = GetDotNetInfo();
            if (!string.IsNullOrWhiteSpace(info))
            {
                _logger.LogInformation("dotnet --info:\n{Info}", info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to log workspace environment: {Message}", ex.Message);
        }
    }

    private static string GetDotNetInfo()
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "dotnet",
                Arguments = "--info",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process process = new() { StartInfo = startInfo };
            process.Start();
            string stdOut = process.StandardOutput.ReadToEnd();
            string stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdOut))
            {
                return stdOut.Trim();
            }

            return stdErr.Trim();
        }
        catch (Exception ex)
        {
            return $"dotnet --info failed: {ex.Message}";
        }
    }

    private void ApplyAssemblyResolver(WorkspaceAssemblySet assemblySet)
    {
        _assemblyResolver?.Dispose();
        _assemblyResolver = new WorkspaceAssemblyResolver(
            assemblySet.All,
            assemblySet.Preferred,
            LogOutput);
    }

    private void LogAssemblySet(
        WorkspaceAssemblySet assemblySet,
        bool hasAnyProjectOutputs,
        bool hasMissingProjectOutputs)
    {
        if (assemblySet.All.Count == 0)
        {
            LogOutput("Warning", "No assemblies discovered for metadata loading.");
            return;
        }

        int preferredCount = assemblySet.Preferred.Count;
        string outputSummary = hasAnyProjectOutputs
            ? string.Format(UiStrings.ProjectOutputsCount, preferredCount)
            : UiStrings.ProjectOutputsNone;
        if (hasMissingProjectOutputs)
        {
            outputSummary += UiStrings.MissingOutputsSuffix;
        }

        LogOutput("Info", $"Assembly resolution: {assemblySet.All.Count} assemblies. {outputSummary}.");

        const int maxList = 20;
        List<string> preferredList = assemblySet.Preferred
            .Take(maxList)
            .Select(path => $"- {path}")
            .ToList();

        if (preferredList.Count > 0)
        {
            LogOutput("Info", "Preferred output assemblies:\n" + string.Join('\n', preferredList));
            if (preferredCount > maxList)
            {
                LogOutput("Info", $"Preferred output list truncated ({preferredCount - maxList} more)." );
            }
        }
    }

    public void Dispose()
    {
        if (_outputLogSinkAccessor is not null)
        {
            _outputLogSinkAccessor.Sink = null;
        }

        if (_extensionContributions is not null)
        {
            _extensionContributions.Changed -= OnExtensionContributionsChanged;
        }

        if (_commandMetadata is not null)
        {
            _commandMetadata.Changed -= OnCommandMetadataChanged;
        }

        if (_extensionViewRegistry is not null)
        {
            _extensionViewRegistry.Changed -= OnExtensionViewsChanged;
        }

        foreach (IDisposable subscription in _extensionCommandSubscriptionsById.Values)
        {
            subscription.Dispose();
        }
        _extensionCommandSubscriptionsById.Clear();

        _outputChannel?.Dispose();

        _statusTextExpiration.Dispose();
        _disposables.Dispose();
        Toolbox.Dispose();
        SolutionExplorer.Dispose();
        References.Dispose();
        ExtensionManager.Dispose();
        foreach (IDisposable view in ExtensionViews.OfType<IDisposable>())
        {
            view.Dispose();
        }

        Collaboration.Dispose();
        AnimationEditor.Dispose();
        Debugger.Dispose();
        InfiniteCanvas.Dispose();
        _assemblyResolver?.Dispose();
        _previewerLaunchService.Dispose();

        foreach (TerminalViewModel terminal in Terminals)
        {
            terminal.Dispose();
        }
        Terminals.Clear();

        foreach (IDisposable subscription in _terminalTitleSubscriptions.Values)
        {
            subscription.Dispose();
        }
        _terminalTitleSubscriptions.Clear();

        foreach (IDisposable subscription in _autoSaveSubscriptions.Values)
        {
            subscription.Dispose();
        }
        _autoSaveSubscriptions.Clear();

        DockFactory.ActiveDockableChanged -= OnActiveDockableChanged;
        DockFactory.DockableClosing -= OnDockableClosing;
        DockFactory.DockableClosed -= OnDockableClosed;

        if (DockLayout is not null)
        {
            DockFactory.SaveLayout(DockLayout, layoutName: CurrentLayoutName);
        }

        DisposeCollectionItems(ExtensionMenuItems);
        DisposeCollectionItems(FileMenuItems);
        DisposeCollectionItems(FileNewMenuItems);
        DisposeCollectionItems(FileNewMenuEntries);
        DisposeCollectionItems(ViewMenuItems);
        DisposeCollectionItems(ServerMenuItems);
        DisposeCollectionItems(LayoutPresetMenuItems);
        DisposeCollectionItems(SavedLayoutMenuItems);
        DisposeCollectionItems(DebugMenuItems);
        DisposeCollectionItems(EditMenuItems);
        DisposeCollectionItems(EditorContextMenuItems);
        DisposeCollectionItems(WorkspaceMenuItems);
        DisposeCollectionItems(ExtensionToolbarItems);
        DisposeCollectionItems(MainToolbarItems);
        ClearStatusBarItems();

        foreach (IEditorDocumentViewModel doc in Documents)
        {
            doc.Dispose();
        }
    }
}

// ==============================================
// Solution Explorer ViewModel
// ==============================================

/// <summary>
/// Represents a node in the Solution Explorer tree.
/// </summary>
public sealed partial class SolutionExplorerNodeViewModel : ReactiveObject
{
    private bool _isStartupProject;
    /// <summary>Gets the display name.</summary>
    public string Name { get; }

    /// <summary>Gets the icon object (image or fallback text).</summary>
    public object? Icon { get; }

    /// <summary>Gets the full path (for files) or project path (for projects).</summary>
    public string? FullPath { get; }

    /// <summary>Gets the project model (for project nodes).</summary>
    public ProjectModel? Project { get; }

    /// <summary>Gets the node kind.</summary>
    public SolutionExplorerNodeKind Kind { get; }

    /// <summary>Gets the child nodes.</summary>
    public ObservableCollection<SolutionExplorerNodeViewModel> Children { get; } = new();

    /// <summary>Gets or sets whether this node is expanded.</summary>
    [Reactive]
    public partial bool IsExpanded { get; set; }

    /// <summary>Gets or sets whether this node is selected.</summary>
    [Reactive]
    public partial bool IsSelected { get; set; }

    /// <summary>Gets or sets whether this project is the startup project.</summary>
    public bool IsStartupProject
    {
        get => _isStartupProject;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _isStartupProject, value))
            {
                this.RaisePropertyChanged(nameof(ProjectToolTip));
            }
        }
    }

    /// <summary>Gets whether this project is runnable.</summary>
    public bool IsRunnableProject => Project?.IsExecutable == true;

    /// <summary>
    /// Gets the tooltip of a project node: whether it is the startup project or an executable project that can
    /// become one; null for libraries and other nodes.
    /// </summary>
    public string? ProjectToolTip => Kind != SolutionExplorerNodeKind.Project ? null
        : IsStartupProject ? UiStrings.StartupProjectToolTip
        : IsRunnableProject ? UiStrings.ExecutableProjectToolTip
        : null;

    /// <summary>Raised when a file node is double-clicked (opened).</summary>
    public event Action<string>? FileOpened;

    /// <summary>
    /// Command to open the associated file.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid>? OpenCommand { get; }

    public SolutionExplorerNodeViewModel(
        string name,
        object? icon,
        SolutionExplorerNodeKind kind,
        string? fullPath = null,
        ProjectModel? project = null)
    {
        Name = name;
        Icon = icon;
        Kind = kind;
        FullPath = fullPath;
        Project = project;

        if ((kind == SolutionExplorerNodeKind.XamlFile || kind == SolutionExplorerNodeKind.File) && fullPath is not null)
        {
            OpenCommand = ReactiveCommand.Create(() => FileOpened?.Invoke(fullPath));
        }
    }

    /// <summary>
    /// Creates a Solution Explorer tree from a WorkspaceModel.
    /// </summary>
    public static SolutionExplorerNodeViewModel FromWorkspace(
        WorkspaceModel workspace,
        string? solutionName = null,
        ISystemIconService? systemIcons = null)
    {
        string rootName = solutionName ?? "Solution";
        SolutionExplorerNodeViewModel root = new(rootName, "🗂", SolutionExplorerNodeKind.Solution);
        root.IsExpanded = true;

        Dictionary<string, SolutionExplorerNodeViewModel> folderNodes =
            new(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<ProjectModel> projects = ProjectSelection.DeduplicateProjectsByPath(workspace.Projects);
        foreach (ProjectModel project in projects)
        {
            object projectIcon = GetIconWithFallback(systemIcons, project.ProjectPath, isDirectory: false, fallbackIcon: "📦");
            SolutionExplorerNodeViewModel projectNode = new(
                project.Name, projectIcon, SolutionExplorerNodeKind.Project, project.ProjectPath, project);

            SolutionExplorerNodeViewModel projectParent = root;
            if (workspace.ProjectFolders.TryGetValue(project.ProjectPath, out string? folderPath) &&
                !string.IsNullOrWhiteSpace(folderPath))
            {
                projectParent = EnsureFolderPath(root, folderNodes, folderPath, systemIcons);
            }

            AddProjectFiles(projectNode, project.Files, systemIcons);
            SortProjectItems(projectNode);

            // The references folder is the first child of the project, whatever the sort option.
            if (project.References.Count > 0)
            {
                object referencesFolderIcon = GetIconWithFallback(systemIcons, null, isDirectory: true, fallbackIcon: "📚");
                SolutionExplorerNodeViewModel refsFolder = new(UiStrings.References, referencesFolderIcon, SolutionExplorerNodeKind.Folder);

                foreach (AssemblyReference asmRef in project.References)
                {
                    object referenceIcon = GetIconWithFallback(systemIcons, asmRef.Path, isDirectory: false, fallbackIcon: "🔗");
                    SolutionExplorerNodeViewModel refNode = new(
                        asmRef.Name, referenceIcon, SolutionExplorerNodeKind.Reference, asmRef.Path);
                    refsFolder.Children.Add(refNode);
                }

                projectNode.Children.Insert(0, refsFolder);
            }

            projectParent.Children.Add(projectNode);
        }

        return root;
    }

    private static void AddProjectFiles(
        SolutionExplorerNodeViewModel projectNode,
        IReadOnlyList<ProjectFileModel> files,
        ISystemIconService? systemIcons)
    {
        Dictionary<string, SolutionExplorerNodeViewModel> folders =
            new(StringComparer.OrdinalIgnoreCase);
        string? projectDirectory = Path.GetDirectoryName(projectNode.FullPath);

        foreach (ProjectFileModel file in files)
        {
            string relative = file.RelativePath.Replace('\\', '/');
            string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // Files outside the project folder, such as generated sources in a redirected intermediate folder or
            // references to another folder, have no place in the project tree.
            if (parts.Length == 0 || Path.IsPathRooted(file.RelativePath) || parts[0] == "..")
            {
                continue;
            }

            SolutionExplorerNodeViewModel current = projectNode;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string folderPath = string.Join('/', parts.Take(i + 1));
                if (!folders.TryGetValue(folderPath, out SolutionExplorerNodeViewModel? folderNode))
                {
                    string folderPathOnDisk = ResolveFolderPath(projectDirectory, folderPath);
                    object folderIcon = GetIconWithFallback(systemIcons, folderPathOnDisk, isDirectory: true, fallbackIcon: "📁");
                    folderNode = new SolutionExplorerNodeViewModel(parts[i], folderIcon, SolutionExplorerNodeKind.Folder, folderPathOnDisk);
                    folders[folderPath] = folderNode;
                    current.Children.Add(folderNode);
                }

                current = folderNode;
            }

            string fileName = parts[^1];
            SolutionExplorerNodeKind kind = IsXamlFile(file.FilePath)
                ? SolutionExplorerNodeKind.XamlFile
                : SolutionExplorerNodeKind.File;

            SolutionExplorerNodeViewModel fileNode = new(
                fileName, GetFileIcon(file.FilePath, systemIcons), kind, file.FilePath);
            current.Children.Add(fileNode);
        }
    }

    // Like Visual Studio, folders come before files.
    private static void SortProjectItems(SolutionExplorerNodeViewModel node)
    {
        if (node.Children.Count == 0)
        {
            return;
        }

        SolutionExplorerNodeViewModel[] ordered = node.Children
            .OrderBy(child => child.Kind == SolutionExplorerNodeKind.Folder ? 0 : 1)
            .ThenBy(child => child.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        node.Children.Clear();
        foreach (SolutionExplorerNodeViewModel child in ordered)
        {
            node.Children.Add(child);
            SortProjectItems(child);
        }
    }

    private static SolutionExplorerNodeViewModel EnsureFolderPath(
        SolutionExplorerNodeViewModel root,
        Dictionary<string, SolutionExplorerNodeViewModel> folderNodes,
        string folderPath,
        ISystemIconService? systemIcons = null)
    {
        string[] segments = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        SolutionExplorerNodeViewModel current = root;
        string currentPath = string.Empty;

        foreach (string segment in segments)
        {
            currentPath = string.IsNullOrEmpty(currentPath)
                ? segment
                : currentPath + "/" + segment;

            if (!folderNodes.TryGetValue(currentPath, out SolutionExplorerNodeViewModel? node))
            {
                object folderIcon = GetIconWithFallback(systemIcons, currentPath, isDirectory: true, fallbackIcon: "📁");
                node = new SolutionExplorerNodeViewModel(segment, folderIcon, SolutionExplorerNodeKind.SolutionFolder);
                folderNodes[currentPath] = node;
                current.Children.Add(node);
            }

            current = node;
        }

        return current;
    }

    private static bool IsXamlFile(string filePath)
    {
        return filePath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static object GetFileIcon(string filePath, ISystemIconService? systemIcons)
    {
        string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
        string fallbackIcon = ext switch
        {
            ".axaml" => "📄",
            ".xaml" => "📄",
            ".cs" => "🧩",
            ".json" => "🧾",
            ".xml" => "🧾",
            ".md" => "📝",
            _ => "📄"
        };

        return GetIconWithFallback(systemIcons, filePath, isDirectory: false, fallbackIcon);
    }

    private static object GetIconWithFallback(
        ISystemIconService? systemIcons,
        string? path,
        bool isDirectory,
        object fallbackIcon)
    {
        return systemIcons?.GetIcon(path, isDirectory, fallbackIcon) ?? fallbackIcon;
    }

    private static string ResolveFolderPath(string? projectDirectory, string relativeFolderPath)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return relativeFolderPath.Replace('/', Path.DirectorySeparatorChar);
        }

        string normalized = relativeFolderPath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(projectDirectory, normalized);
    }
}

/// <summary>
/// Kinds of nodes in the Solution Explorer tree.
/// </summary>
public enum SolutionExplorerNodeKind
{
    /// <summary>Solution root.</summary>
    Solution,

    /// <summary>Project.</summary>
    Project,

    /// <summary>Folder.</summary>
    Folder,

    /// <summary>Solution folder.</summary>
    SolutionFolder,

    /// <summary>XAML file.</summary>
    XamlFile,

    /// <summary>Generic file.</summary>
    File,

    /// <summary>Assembly reference.</summary>
    Reference
}

/// <summary>
/// ViewModel for the Solution Explorer tool panel.
/// </summary>
public sealed partial class SolutionExplorerViewModel : ReactiveObject, ISolutionExplorerPanelModel, IDisposable
{
    private const string FilterPropertyPath = "Item.Name";
    private const string SetStartupProjectCommandId = "workspace.setStartupProject";
    private readonly System.Reactive.Subjects.Subject<object> _revealRequests = new();
    private readonly ISystemIconService? _systemIcons;
    private readonly ICommands? _extensionCommands;
    private readonly IDisposable _filterSubscription;

    public SolutionExplorerNodeViewModel? Root { get; set; }

    public ObservableCollection<SolutionExplorerNodeViewModel> RootItems { get; } = new();

    public HierarchicalModel Model { get; }

    public SortingModel SortingModel { get; }

    public FilteringModel FilteringModel { get; }

    public SearchModel SearchModel { get; }

    [Reactive]
    public partial object? SelectedRow { get; set; }

    [Reactive]
    public partial SolutionExplorerNodeViewModel? SelectedNode { get; private set; }

    /// <summary>Gets or sets whether the panel is visible.</summary>
    [Reactive]
    public partial bool IsVisible { get; set; }

    /// <summary>Gets or sets the filter text.</summary>
    [Reactive]
    public partial string? FilterText { get; set; }

    /// <summary>Raised when a XAML file is opened from the tree.</summary>
    public event Action<string>? FileOpenRequested;

    public ReactiveCommand<RxVoid, RxVoid> OpenSelectedCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> SetStartupProjectCommand { get; }

    public SolutionExplorerViewModel(ISystemIconService? systemIcons = null, ICommands? extensionCommands = null)
    {
        _systemIcons = systemIcons;
        _extensionCommands = extensionCommands;
        SortingModel = new SortingModel();
        FilteringModel = new FilteringModel();
        SearchModel = new SearchModel
        {
            HighlightMode = SearchHighlightMode.TextAndCell,
            HighlightCurrent = true,
            WrapNavigation = true,
            UpdateSelectionOnNavigate = true
        };

        var options = new HierarchicalOptions
        {
            ChildrenSelector = item => ((SolutionExplorerNodeViewModel)item).Children,
            IsLeafSelector = item => ((SolutionExplorerNodeViewModel)item).Children.Count == 0,
            IsExpandedSelector = item => ((SolutionExplorerNodeViewModel)item).IsExpanded,
            IsExpandedSetter = (item, value) => ((SolutionExplorerNodeViewModel)item).IsExpanded = value,
            VirtualizeChildren = true,
            AllowExpandToItemSearch = true
        };

        Model = new HierarchicalModel(options);
        Model.SetRoots(RootItems);

        this.WhenAnyValue(x => x.SelectedRow)
            .Subscribe(row => SelectedNode = row switch
            {
                HierarchicalNode<SolutionExplorerNodeViewModel> node => node.Item,
                HierarchicalNode node => node.Item as SolutionExplorerNodeViewModel,
                SolutionExplorerNodeViewModel node => node,
                _ => null
            });

        _filterSubscription = this.WhenAnyValue(x => x.FilterText)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplyFilterAndSearch);

        IObservable<bool> canOpen = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node is not null
                            && (node.Kind == SolutionExplorerNodeKind.File || node.Kind == SolutionExplorerNodeKind.XamlFile)
                            && !string.IsNullOrWhiteSpace(node.FullPath));
        IObservable<bool> canSetStartup = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => _extensionCommands is not null
                            && node?.Kind == SolutionExplorerNodeKind.Project
                            && node.Project?.IsExecutable == true);

        OpenSelectedCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedNode?.FullPath is { } filePath)
            {
                FileOpenRequested?.Invoke(filePath);
            }
        }, canOpen);

        SetStartupProjectCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedNode?.Kind == SolutionExplorerNodeKind.Project &&
                SelectedNode.Project is not null)
            {
                await _extensionCommands!.ExecuteAsync(
                    SetStartupProjectCommandId,
                    new object?[]
                    {
                        SelectedNode.Project.ProjectPath,
                        SelectedNode.Project.TargetFramework
                    },
                    CancellationToken.None);
            }
        }, canSetStartup);
    }

    public void SetStartupProject(ProjectModel? project)
    {
        if (Root is null)
        {
            return;
        }

        foreach (SolutionExplorerNodeViewModel node in EnumerateNodes(Root))
        {
            if (node.Kind == SolutionExplorerNodeKind.Project &&
                node.Project is not null)
            {
                bool matches = project is not null &&
                               (ReferenceEquals(node.Project, project)
                                 || (FileSystemPathComparison.Equals(node.Project.ProjectPath, project.ProjectPath)
                                    && (string.IsNullOrWhiteSpace(project.TargetFramework)
                                        || string.Equals(node.Project.TargetFramework, project.TargetFramework, StringComparison.OrdinalIgnoreCase))));
                node.IsStartupProject = matches;
            }
            else
            {
                node.IsStartupProject = false;
            }
        }
    }

    public void SetStartupProjectPath(string? projectPath)
    {
        if (Root is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            SetStartupProject(null);
            return;
        }

        ProjectModel? match = EnumerateNodes(Root)
            .Select(node => node.Project)
            .FirstOrDefault(project => project is not null &&
                                       FileSystemPathComparison.Equals(project.ProjectPath, projectPath));
        SetStartupProject(match);
    }

    /// <summary>
    /// Expands the tree down to the node of a file and selects it; returns false when no node shows the file, for
    /// example because it lies outside the workspace or the filter hides it.
    /// </summary>
    public bool RevealFile(string filePath)
    {
        SolutionExplorerNodeViewModel? match = RootItems.SelectMany(EnumerateNodes)
            .FirstOrDefault(node => node.Kind is SolutionExplorerNodeKind.File or SolutionExplorerNodeKind.XamlFile
                && node.FullPath is not null && FileSystemPathComparison.Equals(node.FullPath, filePath));
        if (match is null)
        {
            return false;
        }

        if (!Model.TryExpandToItem(match, out HierarchicalNode? row) || row is null)
        {
            return false;
        }

        SelectedRow = row;
        _revealRequests.OnNext(row);
        return true;
    }

    /// <summary>Gets the rows that <see cref="RevealFile"/> selected and the view scrolls into view.</summary>
    public IObservable<object> RevealRequests => _revealRequests;

    private static IEnumerable<SolutionExplorerNodeViewModel> EnumerateNodes(SolutionExplorerNodeViewModel root)
    {
        Queue<SolutionExplorerNodeViewModel> queue = new();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            SolutionExplorerNodeViewModel node = queue.Dequeue();
            yield return node;
            foreach (SolutionExplorerNodeViewModel child in node.Children)
            {
                queue.Enqueue(child);
            }
        }
    }

    /// <summary>
    /// Loads a workspace into the Solution Explorer.
    /// </summary>
    public void LoadWorkspace(WorkspaceModel workspace, string? solutionName = null)
    {
        _expandedBeforeFilter = null;
        Root = SolutionExplorerNodeViewModel.FromWorkspace(workspace, solutionName, _systemIcons);
        CollapseAll(Root);
        WireFileOpen(Root);
        SetRoot(Root);
    }

    public void Clear()
    {
        _expandedBeforeFilter = null;
        Root = null;
        RootItems.Clear();
        Model.Refresh();
        ApplyFilterAndSearch(FilterText);
    }

    private void SetRoot(SolutionExplorerNodeViewModel? root)
    {
        RootItems.Clear();
        if (root is not null)
        {
            RootItems.Add(root);
        }

        Model.Refresh();
        ApplyFilterAndSearch(FilterText);
    }

    private static void CollapseAll(SolutionExplorerNodeViewModel node)
    {
        foreach (SolutionExplorerNodeViewModel child in node.Children)
        {
            child.IsExpanded = false;
            CollapseAll(child);
        }
    }

    private void WireFileOpen(SolutionExplorerNodeViewModel node)
    {
        node.FileOpened += path => FileOpenRequested?.Invoke(path);

        foreach (SolutionExplorerNodeViewModel child in node.Children)
        {
            WireFileOpen(child);
        }
    }

    internal void ApplyFilterAndSearch(string? text)
    {
        ApplyFiltering(text);
        ApplySearch(text);
    }

    // The grid filters only the rows of expanded folders, so the folders above every match are expanded while a filter
    // is set; clearing the filter restores the folders the user had expanded.
    private void ApplyFiltering(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            FilteringModel.Remove(FilterPropertyPath);
            if (_expandedBeforeFilter is { } expanded)
            {
                _expandedBeforeFilter = null;
                foreach (SolutionExplorerNodeViewModel node in RootItems.SelectMany(EnumerateNodes))
                    node.IsExpanded = expanded.Contains(node);
                Model.Refresh();
            }

            return;
        }

        _expandedBeforeFilter ??= RootItems.SelectMany(EnumerateNodes).Where(node => node.IsExpanded).ToHashSet();
        string query = text.Trim();
        HashSet<object> matches = BuildMatchSet(RootItems, query);
        Model.Refresh();
        FilteringModel.SetOrUpdate(new FilteringDescriptor(
            columnId: FilterPropertyPath,
            @operator: FilteringOperator.Custom,
            propertyPath: FilterPropertyPath,
            predicate: item => MatchesFilter(item, matches)));
    }

    private HashSet<SolutionExplorerNodeViewModel>? _expandedBeforeFilter;

    private void ApplySearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SearchModel.Clear();
            return;
        }

        string query = text.Trim();
        SearchModel.SetOrUpdate(new SearchDescriptor(
            query,
            matchMode: SearchMatchMode.Contains,
            termMode: SearchTermCombineMode.Any,
            scope: SearchScope.VisibleColumns,
            comparison: StringComparison.OrdinalIgnoreCase));
    }

    // The grid hands over its rows, which wrap the tree nodes.
    private static bool MatchesFilter(object? item, HashSet<object> matches)
    {
        object? node = item is HierarchicalNode row ? row.Item : item;
        return node is not null && matches.Contains(node);
    }

    private static HashSet<object> BuildMatchSet(
        IEnumerable<SolutionExplorerNodeViewModel> roots,
        string text)
    {
        HashSet<object> matches = new();
        foreach (SolutionExplorerNodeViewModel root in roots)
        {
            CollectMatches(root, text, matches);
        }

        return matches;
    }

    private static bool CollectMatches(
        SolutionExplorerNodeViewModel node,
        string text,
        HashSet<object> matches)
    {
        bool isMatch = node.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
        bool childMatch = false;

        foreach (SolutionExplorerNodeViewModel child in node.Children)
        {
            if (CollectMatches(child, text, matches))
            {
                childMatch = true;
            }
        }

        // An ancestor of a match opens, so the match becomes a visible row.
        if (childMatch)
        {
            node.IsExpanded = true;
        }

        if (isMatch)
        {
            matches.Add(node);
            AddDescendants(node, matches);
            return true;
        }

        if (childMatch)
        {
            matches.Add(node);
            return true;
        }

        return false;
    }

    private static void AddDescendants(SolutionExplorerNodeViewModel node, HashSet<object> matches)
    {
        foreach (SolutionExplorerNodeViewModel child in node.Children)
        {
            if (matches.Add(child))
            {
                AddDescendants(child, matches);
            }
        }
    }

    /// <summary>Stops the pending filter timer.</summary>
    public void Dispose() => _filterSubscription.Dispose();
}
