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

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>
/// Common interface for editor documents.
/// </summary>
public interface IEditorDocumentViewModel : IDisposable
{
    string FilePath { get; }
    string FileName { get; }
    bool IsModified { get; }
    int CurrentLine { get; }
    int CurrentColumn { get; }
    ReactiveCommand<Unit, Unit> SaveCommand { get; }
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
    private readonly Dictionary<string, (DateTime LastWriteUtc, string? ClassName)> _xamlClassCache
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
    /// Gets the title for display (includes dirty indicator).
    /// </summary>
    public string Title => IsModified ? $"{FileName}*" : FileName;

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
    /// Gets or sets the selected AST node ID (synced between editor, tree, and designer).
    /// </summary>
    [Reactive]
    public partial Guid? SelectedNodeId { get; set; }

    /// <summary>
    /// Gets the source of the most recent selection change.
    /// </summary>
    public SyncSource SelectionSource => _selectionSource;

    /// <summary>
    /// Updates selection with an explicit source to avoid feedback loops.
    /// </summary>
    public void SetSelectedNode(Guid? nodeId, SyncSource source)
    {
        _selectionSource = source;
        SelectedNodeId = nodeId;
    }

    /// <summary>
    /// Command to save the document.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    /// <summary>
    /// Command to close the document.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public event Action<DesignerDocumentViewModel>? CloseRequested;

    /// <summary>
    /// Command to switch to design view.
    /// </summary>
    public ReactiveCommand<Unit, Unit> DesignViewCommand { get; }

    /// <summary>
    /// Command to switch to code view.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CodeViewCommand { get; }

    /// <summary>
    /// Command to switch to split view.
    /// </summary>
    public ReactiveCommand<Unit, Unit> SplitViewCommand { get; }

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

    /// <summary>
    /// Command to navigate to the selected control definition.
    /// </summary>
    public ReactiveCommand<Unit, Unit> NavigateToDefinitionCommand { get; }

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
            ReactiveCommand<Unit, Unit> start = ReactiveCommand.CreateFromTask(
                () => startPreviewerAsync(this), this.WhenAnyValue(document => document.IsLoaded));
            StartPreviewerCommand = start;
            _disposables.Add(start);
            _disposables.Add(start.ThrownExceptions.ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(error => SetPreviewerLaunchError(error.Message)));
            UseExternalPreviewer = _workspaceProvider?.Invoke() is { Projects.Count: > 0 };
        }

        // Track modification state
        InitializeDesignerFileState();
        IDisposable isModifiedSubscription = this.WhenAnyValue(x => x.CodeEditor.IsModified)
            .Subscribe(_ => UpdateFileModifiedState());
        _disposables.Add(isModifiedSubscription);

        // Watch for property changes to raise Title notification
        IDisposable titleSubscription = this.WhenAnyValue(x => x.IsModified)
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
                        if (source != SyncSource.CodeEditor)
                        {
                            CodeEditor.SetCaretOffsetFromSync(offset.Value);
                        }
                    }

                    if (source != SyncSource.DesignSurface)
                    {
                        DesignSurface.SelectByAstNodeIdFromSync(id.Value);
                    }
                }
            });
        _disposables.Add(selectedNodeSyncSubscription);

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
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                // After any sync, commit pending changes as an undo batch
                SyncEngine.CommitUndoBatch("Sync");

                // Layout-Änderungen können die bestehenden Control-Instanzen behalten.
                DesignSurface.RequestRebuild(allowLayoutUpdate: true);
            });
        _disposables.Add(syncEventsSubscription);

        SaveCommand = ReactiveCommand.CreateFromTask(SaveDocumentFileAsync,
            this.WhenAnyValue(document => document.IsLoading).Select(loading => !loading));

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

        IDisposable previewerToggleSubscription = this.WhenAnyValue(
                x => x.UseExternalPreviewer, x => x.IsLoaded, x => x.StartPreviewerCommand)
            .Where(state => state.Item1 && state.Item2 && state.Item3 is not null)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(state =>
            {
                if (!IsDisposed && IsLoaded && UseExternalPreviewer &&
                    ReferenceEquals(state.Item3, StartPreviewerCommand) && state.Item3!.CanExecute(null))
                    state.Item3.Execute(null);
            });
        _disposables.Add(previewerToggleSubscription);

        IDisposable breakpointSourceSubscription = this.WhenAnyValue(x => x.Breakpoints)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
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

        string? targetPath = FindXamlFileForType(project, type.FullName);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        await _openFileAsync(targetPath);
    }

    private string? FindXamlFileForType(ProjectModel project, string fullTypeName)
    {
        foreach (XamlFileModel file in project.XamlFiles)
        {
            string? className = TryGetXamlClassName(file.FilePath);
            if (string.IsNullOrWhiteSpace(className))
            {
                continue;
            }

            if (string.Equals(className, fullTypeName, StringComparison.Ordinal))
            {
                return file.FilePath;
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
        catch
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
public sealed partial class TextDocumentViewModel : ReactiveObject, IEditorDocumentViewModel
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

    [Reactive]
    public partial bool WordWrap { get; set; }

    [Reactive]
    public partial bool ShowLineNumbers { get; set; } = true;

    [Reactive]
    public partial double FontSize { get; set; } = 14.0;

    public string? LanguageId { get; }

    public ObservableCollection<LanguageDiagnostic> Diagnostics { get; } = new();

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

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    public TextDocumentViewModel(string filePath, ILanguageIntellisenseRegistry? languageRegistry = null)
    {
        FilePath = filePath;
        TrackDocumentHistory();
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
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(evt => { _ = RefreshDiagnosticsAsync(); });
        _disposables.Add(diagnosticsSubscription);

        IDisposable semanticTokensSubscription = Observable.FromEventPattern<EventHandler, EventArgs>(
            h => Document.TextChanged += h,
            h => Document.TextChanged -= h)
            .Where(_ => !_suppressTextChanged && _languageService is not null)
            .Throttle(TimeSpan.FromMilliseconds(500))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
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

        if (_diagnosticsSource is not null)
        {
            _diagnosticsSource.DiagnosticsChanged += OnDiagnosticsChanged;
            _disposables.Add(Disposable.Create(() => _diagnosticsSource.DiagnosticsChanged -= OnDiagnosticsChanged));
        }

        IDisposable breakpointSourceSubscription = this.WhenAnyValue(x => x.Breakpoints)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
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

        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            Diagnostics.Clear();
            foreach (LanguageDiagnostic diagnostic in diagnostics)
            {
                Diagnostics.Add(diagnostic);
            }

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
            .ObserveOn(RxSchedulers.MainThreadScheduler)
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
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    /// <summary>
    /// Command to copy the selected message.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CopySelectedCommand { get; }

    /// <summary>
    /// Command to copy all messages.
    /// </summary>
    public ReactiveCommand<Unit, Unit> CopyAllCommand { get; }

    /// <summary>
    /// Gets or sets the selected output message.
    /// </summary>
    [Reactive]
    public partial OutputMessage? SelectedMessage { get; set; }

    /// <summary>
    /// Interaction to copy output text to the clipboard.
    /// </summary>
    public Interaction<string, Unit> CopyToClipboardInteraction { get; } = new();

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
public sealed partial class ReferencesViewModel : ReactiveObject
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

    public ReactiveCommand<ReferenceLocationViewModel, Unit> OpenLocationCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenSelectedCommand { get; }

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
            .ObserveOn(RxSchedulers.MainThreadScheduler)
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

    public Interaction<ReferenceLocationViewModel?, Unit> CloseInteraction { get; } = new();

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

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

    public Interaction<LanguageCodeAction?, Unit> CloseInteraction { get; } = new();

    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

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

    public Interaction<string?, Unit> CloseInteraction { get; } = new();

    public ReactiveCommand<Unit, Unit> ConfirmCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

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

    public Interaction<PreviewerTrustDecision, Unit> CloseInteraction { get; } = new();

    public ReactiveCommand<Unit, Unit> TrustCommand { get; }
    public ReactiveCommand<Unit, Unit> AllowOnceCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

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

    public Interaction<bool, Unit> CloseInteraction { get; } = new();

    public ReactiveCommand<Unit, Unit> AllowCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

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

/// <summary>Bestimmt die Öffnungsart eines Eintrags in der Historie.</summary>
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
    private readonly IEditorThemeService? _editorThemes;

    public ReactiveCommand<Unit, Unit> OpenOptionsCommand { get; }
    public Interaction<OptionsDialogViewModel, bool> OptionsInteraction { get; } = new();
    private readonly CompositeDisposable _disposables = new();
    private readonly SerialDisposable _statusTextExpiration = new();
    private string _statusText = UiStrings.Ready;
    private long _statusTextGeneration;
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

    /// <summary>Enthält die Einträge unter Ansicht → Server.</summary>
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

    private void SetPersistentStatus(string message) => SetStatusText(message, persistent: true);

    private void SetStatusText(string message, bool persistent)
    {
        long generation = Interlocked.Increment(ref _statusTextGeneration);
        this.RaiseAndSetIfChanged(ref _statusText, message);
        _statusTextExpiration.Disposable = null;
        if (persistent || string.IsNullOrWhiteSpace(message) || string.Equals(message, UiStrings.Ready, StringComparison.Ordinal))
            return;

        _statusTextExpiration.Disposable = Observable.Timer(TimeSpan.FromSeconds(30))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (Interlocked.Read(ref _statusTextGeneration) == generation)
                    StatusText = string.Empty;
            });
    }

    /// <summary>
    /// Gets the recent files list.
    /// </summary>
    public ObservableCollection<RecentFileEntry> RecentFiles { get; } = new();

    /// <summary>Die jeweils zehn neuesten Einträge je Öffnungsart.</summary>
    public IEnumerable<RecentFileEntry> RecentWorkspaces => RecentFiles.Where(entry => entry.Kind == RecentEntryKind.Workspace).Take(RecentFilesLimit);
    public IEnumerable<RecentFileEntry> RecentDocuments => RecentFiles.Where(entry => entry.Kind == RecentEntryKind.File).Take(RecentFilesLimit);
    public IEnumerable<RecentFileEntry> RecentFolders => RecentFiles.Where(entry => entry.Kind == RecentEntryKind.Folder).Take(RecentFilesLimit);
    public bool HasRecentWorkspaces => RecentWorkspaces.Any();
    public bool HasRecentDocuments => RecentDocuments.Any();
    public bool HasRecentFolders => RecentFolders.Any();

    public bool HasRecentFiles => RecentFiles.Count > 0;
    public ReactiveCommand<RecentFileEntry, Unit> OpenRecentCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearRecentFilesCommand { get; }
    public ReactiveCommand<Unit, Unit> ManageRecentFilesCommand { get; }
    public Interaction<RecentFilesDialogViewModel, Unit> RecentFilesInteraction { get; } = new();

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
    public Interaction<Unit, string?> OpenFileInteraction { get; } = new();

    /// <summary>
    /// Wählt eine Projekt- oder Solution-Datei als Workspace aus.
    /// </summary>
    public Interaction<Unit, WorkspaceOpenRequest?> OpenWorkspaceInteraction { get; } = new();

    /// <summary>
    /// Interaction for selecting an extension package.
    /// </summary>
    public Interaction<Unit, string?> ExtensionPackageOpenInteraction { get; } = new();

    public Interaction<Unit, Unit> ExtensionManagerInteraction { get; } = new();

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
    public ReactiveCommand<Unit, Unit> NewDocumentCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDocumentCommand { get; }
    /// <summary>Öffnet den Dialog zur Workspace-Auswahl.</summary>
    public ReactiveCommand<Unit, Unit> OpenWorkspaceCommand { get; }
    /// <summary>Schließt die aktive Datei.</summary>
    public ReactiveCommand<Unit, Unit> CloseActiveDocumentCommand { get; }
    /// <summary>Schließt alle Dateien und entlädt den Workspace.</summary>
    public ReactiveCommand<Unit, Unit> CloseWorkspaceCommand { get; }
    public ReactiveCommand<string, Unit> OpenPathCommand { get; }
    public ReactiveCommand<IReadOnlyList<string>, Unit> OpenPathsCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveDocumentCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveAllCommand { get; }
    public ReactiveCommand<Unit, Unit> ExitCommand { get; }

    // Save behavior commands
    public ReactiveCommand<Unit, Unit> SetAutoSaveCommand { get; }
    public ReactiveCommand<Unit, Unit> SetManualSaveCommand { get; }
    public ReactiveCommand<Unit, Unit> SetNoSaveCommand { get; }

    // Theme commands
    public ReactiveCommand<Unit, Unit> SetThemeDefaultCommand { get; }
    public ReactiveCommand<Unit, Unit> SetThemeLightCommand { get; }
    public ReactiveCommand<Unit, Unit> SetThemeDarkCommand { get; }

    // Extension-owned non-lifecycle shell command handlers
    private ReactiveCommand<Unit, Unit> UndoCommand { get; }
    private ReactiveCommand<Unit, Unit> RedoCommand { get; }
    private ReactiveCommand<Unit, Unit> CutCommand { get; }
    private ReactiveCommand<Unit, Unit> CopyCommand { get; }
    private ReactiveCommand<Unit, Unit> PasteCommand { get; }
    private ReactiveCommand<Unit, Unit> DuplicateCommand { get; }
    private ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    private ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
    private ReactiveCommand<Unit, Unit> RenameSymbolCommand { get; }
    private ReactiveCommand<Unit, Unit> FormatDocumentCommand { get; }
    private ReactiveCommand<Unit, Unit> CodeActionsCommand { get; }
    private ReactiveCommand<Unit, Unit> DocumentSymbolsCommand { get; }
    private ReactiveCommand<Unit, Unit> WorkspaceSymbolsCommand { get; }

    // View Commands
    private ReactiveCommand<Unit, Unit> ToggleBreakpointsCommand { get; }
    private ReactiveCommand<Unit, Unit> ToggleCallStackCommand { get; }
    private ReactiveCommand<Unit, Unit> ToggleLocalsCommand { get; }
    private ReactiveCommand<Unit, Unit> ToggleWatchesCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenExtensionManagerCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLayoutCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveLayoutCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveLayoutAsCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteLayoutCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCanvasCommand { get; }

    // Help Commands
    public ReactiveCommand<Unit, Unit> AboutCommand { get; }

    // Previewer Commands
    public ReactiveCommand<Unit, Unit> StartPreviewerCommand { get; }

    // Extension-owned non-lifecycle shell command handlers
    private ReactiveCommand<Unit, Unit> StartDebugCommand { get; }
    private ReactiveCommand<Unit, Unit> StopDebugCommand { get; }
    private ReactiveCommand<Unit, Unit> ContinueDebugCommand { get; }
    private ReactiveCommand<Unit, Unit> StepOverCommand { get; }
    private ReactiveCommand<Unit, Unit> StepInCommand { get; }
    private ReactiveCommand<Unit, Unit> StepOutCommand { get; }
    private ReactiveCommand<Unit, Unit> PauseDebugCommand { get; }
    private ReactiveCommand<Unit, Unit> ToggleBreakpointCommand { get; }
    private ReactiveCommand<Unit, Unit> StartRunCommand { get; }
    private ReactiveCommand<Unit, Unit> StopRunCommand { get; }

    // Terminal Commands
    private ReactiveCommand<Unit, Unit> NewTerminalCommand { get; }

    // Command palette
    public ReactiveCommand<Unit, Unit> ShowCommandPaletteCommand { get; }

    /// <summary>
    /// Command to close a specific document (used by tab close button).
    /// </summary>
    public ReactiveCommand<IEditorDocumentViewModel, Unit> CloseDocumentCommand { get; }

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
        IEditorThemeService? editorThemes = null)
    {
        _recentFilesPath = recentFilesPath;
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
                        StatusText = XamlVisualEditor.Core.Localization.UiStrings.Ready;
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
            () => ExtensionPackageOpenInteraction.Handle(Unit.Default).ToTask());
        InitializeThemePackages(extensionManager);
        SolutionExplorer = new SolutionExplorerViewModel(systemIconService, _extensionCommands);

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
        IRootDock? persistedLayout = DockFactory.LoadLayout();
        IRootDock layout = persistedLayout ?? DockFactory.CreateDefaultLayout();
        XamlEditorDockFactory.EnsureLayoutDefaults(layout);
        DockFactory.InitLayout(layout);
        DockFactory.EnsureOwnerReferences(layout);
        DockFactory.ConfigureToolViewModels(layout);
        DockFactory.ConfigureDocumentViewModels(layout);
        DockLayout = layout;
        WireDockEvents();


        IObservable<int> activeLine = this.WhenAnyValue(x => x.ActiveDocument)
            .Select(doc => doc is null
                ? Observable.Return(1)
                : ObserveDocumentProperty(doc, nameof(IEditorDocumentViewModel.CurrentLine), () => doc.CurrentLine))
            .Switch();
        _activeLine = activeLine.ToProperty(this, x => x.ActiveLine);
        _disposables.Add(_activeLine);

        IObservable<int> activeColumn = this.WhenAnyValue(x => x.ActiveDocument)
            .Select(doc => doc is null
                ? Observable.Return(1)
                : ObserveDocumentProperty(doc, nameof(IEditorDocumentViewModel.CurrentColumn), () => doc.CurrentColumn))
            .Switch();
        _activeColumn = activeColumn.ToProperty(this, x => x.ActiveColumn);
        _disposables.Add(_activeColumn);

        _dockDocuments.Clear();
        foreach (IEditorDocumentViewModel doc in Documents)
        {
            AddDocumentToDock(doc);
        }

        InfiniteCanvas.UpdateOpenDocuments(Documents);
        Documents.CollectionChanged += (_, _) => InfiniteCanvas.UpdateOpenDocuments(Documents);

        // File commands
        NewDocumentCommand = ReactiveCommand.CreateFromTask(NewDocumentAsync);
        OpenDocumentCommand = ReactiveCommand.CreateFromTask(() => OpenDocumentAsync(openWorkspace: false));
        OpenWorkspaceCommand = ReactiveCommand.CreateFromTask(() => OpenDocumentAsync(openWorkspace: true));
        CloseActiveDocumentCommand = ReactiveCommand.Create(
            () => { if (ActiveDocument is { } document) CloseDocument(document); },
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

        OpenExtensionManagerCommand = ReactiveCommand.CreateFromObservable(
            () => ExtensionManagerInteraction.Handle(Unit.Default));
        RefreshExtensionContributions();
        UpdateFileNewMenuEntries();
        SyncExtensionDockables();
        if (persistedLayout is null) DockFactory.CollapseBottomToolDock(DockLayout);

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
            if (await OptionsInteraction.Handle(dialog)) StatusText = XamlVisualEditor.Core.Localization.UiStrings.OptionsSaved;
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
        ExitCommand = ReactiveCommand.Create(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
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
                    document.CodeEditor.SelectionLength = 0;
                    document.CodeEditor.CaretOffset = start;
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
            this.WhenAnyValue(viewModel => viewModel.ActiveDesignerDocument)
                .Select(document => document is null ? Observable.Return(false) : document.WhenAnyValue(value => value.IsLoaded))
                .Switch().CombineLatest(hasWorkspace, (loaded, hasWs) => loaded && hasWs));

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
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshExtensionCommandEnablement());
        _disposables.Add(extensionCommandEnablementSubscription);

        // Close document command (used by tab close buttons)
        CloseDocumentCommand = ReactiveCommand.Create<IEditorDocumentViewModel>(doc =>
        {
            CloseDocument(doc);
        });

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
        IDisposable activeDocumentSyncSubscription = this.WhenAnyValue(x => x.ActiveDesignerDocument)
            .Where(d => d is not null && !d.IsDisposed)
            .Select(d => d!.SyncEngine.SyncEvents.Select(_ => d))
            .Switch()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
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

                doc.PreviewerSession = _previewerLaunchService.TryGetSession(doc.FilePath, out PreviewerTcpSession? session)
                    ? session
                    : null;
            });
        _disposables.Add(previewerSessionSubscription);

        _previewerLaunchService.PreviewerErrorReceived += OnPreviewerErrorReceived;
        _disposables.Add(Disposable.Create(() =>
            _previewerLaunchService.PreviewerErrorReceived -= OnPreviewerErrorReceived));

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
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(UpdateExecutionLocationFromFrame);
        _disposables.Add(executionFrameSubscription);

        IDisposable activeDocumentDiagnosticsSubscription = this.WhenAnyValue(x => x.ActiveDesignerDocument)
            .Select(doc => doc is null
                ? Observable.Return<(DesignerDocumentViewModel? Doc, IReadOnlyList<XamlDiagnostic> Diags)>(
                    (null, Array.Empty<XamlDiagnostic>()))
                : doc.SyncEngine.SyncEvents.Select(e =>
                    (Doc: (DesignerDocumentViewModel?)doc,
                        Diags: (IReadOnlyList<XamlDiagnostic>)(e.Diagnostics ?? Array.Empty<XamlDiagnostic>()))))
            .Switch()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(result =>
            {
                string? filePath = result.Doc?.FilePath;
                Output.ReplaceDiagnostics(result.Diags, filePath);
                LogDiagnosticsSummary(result.Diags);
            });
        _disposables.Add(activeDocumentDiagnosticsSubscription);

        IDisposable activeTextDiagnosticsSubscription = this.WhenAnyValue(x => x.ActiveTextDocument)
            .Select(doc => doc is null
                ? Observable.Empty<IReadOnlyList<LanguageDiagnostic>>()
                : Observable.FromEventPattern<System.Collections.Specialized.NotifyCollectionChangedEventHandler,
                        System.Collections.Specialized.NotifyCollectionChangedEventArgs>(
                        h => doc.Diagnostics.CollectionChanged += h,
                        h => doc.Diagnostics.CollectionChanged -= h)
                    .Select(_ => (IReadOnlyList<LanguageDiagnostic>)doc.Diagnostics.ToList())
                    .StartWith((IReadOnlyList<LanguageDiagnostic>)doc.Diagnostics.ToList()))
            .Switch()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
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
        IDisposable selectionSyncSubscription = this.WhenAnyValue(x => x.ActiveDesignerDocument)
            .Select(doc => doc is null || doc.IsDisposed
                ? Observable.Return<Guid?>(null)
                : doc.WhenAnyValue(d => d.SelectedNodeId))
            .Switch()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(id => ApplySelectionToTrees(id));
        _disposables.Add(selectionSyncSubscription);

        // Sync grid selections back to the active document
        IDisposable visualTreeSelectionSubscription = this.WhenAnyValue(x => x.VisualTree.SelectedNode)
            .CombineLatest(this.WhenAnyValue(x => x.ActiveDesignerDocument), (node, doc) => (node, doc))
            .Where(t => t.doc is not null && !t.doc.IsDisposed)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Where(_ => !_suppressTreeSelectionSync && !AreExtensionTreePanelsVisible())
            .Subscribe(t => t.doc!.SetSelectedNode(t.node?.AstNodeId, SyncSource.TreeView));
        _disposables.Add(visualTreeSelectionSubscription);

        IDisposable logicalTreeSelectionSubscription = this.WhenAnyValue(x => x.LogicalTree.SelectedNode)
            .CombineLatest(this.WhenAnyValue(x => x.ActiveDesignerDocument), (node, doc) => (node, doc))
            .Where(t => t.doc is not null && !t.doc.IsDisposed)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Where(_ => !_suppressTreeSelectionSync && !AreExtensionTreePanelsVisible())
            .Subscribe(t => t.doc!.SetSelectedNode(t.node?.AstNodeId, SyncSource.TreeView));
        _disposables.Add(logicalTreeSelectionSubscription);

        // Update title when active document changes
        IDisposable titleUpdateSubscription = this.WhenAnyValue(x => x.ActiveDocument)
            .Select(doc => doc is not null ? $"XAML Visual Editor — {doc.FileName}" : "XAML Visual Editor")
            .Subscribe(t => Title = t);
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
        ReactiveCommand<Unit, Unit> reactiveCommand,
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
        ReactiveCommand<Unit, Unit> reactiveCommand,
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
        return this.WhenAnyValue(x => x.ActiveDesignerDocument)
            .Select(doc => doc is null
                ? Observable.Return(false)
                : ObserveDocumentState(doc, evaluator, changeSources))
            .Switch();
    }

    private IObservable<bool> ObserveTextDocumentState(
        Func<TextDocumentViewModel, bool> evaluator,
        params Func<TextDocumentViewModel, IObservable<Unit>>[] changeSources)
    {
        return this.WhenAnyValue(x => x.ActiveTextDocument)
            .Select(doc => doc is null
                ? Observable.Return(false)
                : ObserveDocumentState(doc, evaluator, changeSources))
            .Switch();
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
            ReactiveCommand<Unit, Unit> command = ReactiveCommand.Create(() => RunDesignerEdit(() =>
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
            designer.CodeEditor.SelectionLength = 0;
            designer.CodeEditor.CaretOffset = start;
        }
        else
        {
            text!.SelectionLength = 0;
            text.CaretOffset = start;
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
            designer.CodeEditor.SelectionStart = start + clipboard.Length;
            designer.CodeEditor.SelectionLength = 0;
            designer.CodeEditor.CaretOffset = start + clipboard.Length;
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
        document.SelectionStart = nextOffset;
        document.SelectionLength = 0;
        document.SetCaretOffset(nextOffset);
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
        document.SelectionStart = offset;
        document.SelectionLength = 0;
        document.SetCaretOffset(offset);
    }

    private static void SelectAllText(TextDocumentViewModel? document)
    {
        if (document is null)
        {
            return;
        }

        document.SelectionStart = 0;
        document.SelectionLength = document.Document.TextLength;
        document.SetCaretOffset(document.Document.TextLength);
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
            StatusText = string.Format(UiStrings.DocumentCreated, (object?)(doc.FileName));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to load new document: {Message}", ex.Message);
            SetPersistentStatus(string.Format(UiStrings.DocumentCreateFailed, (object?)(ex.Message)));
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
                WorkspaceOpenRequest? request = await OpenWorkspaceInteraction.Handle(Unit.Default);
                if (request is null) return;
                await LoadWorkspaceAsync(request.FilePath);
                if (HasWorkspace && FileSystemPathComparison.Equals(_workspacePath, request.FilePath))
                    AddRecentFile(request.FilePath, RecentEntryKind.Workspace);
                return;
            }
            string? filePath = await OpenFileInteraction.Handle(Unit.Default);
            if (!string.IsNullOrEmpty(filePath))
            {
                await OpenFileAsync(filePath, allowWorkspaceLoad: openWorkspace);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Open document failed: {Message}", ex.Message);
            SetPersistentStatus(string.Format(UiStrings.DocumentOpenFailed, (object?)(ex.Message)));
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

            await ActiveDocument.SaveCommand.Execute();
            StatusText = string.Format(UiStrings.DocumentSaved, (object?)(ActiveDocument.FileName));
        }
    }

    private async System.Threading.Tasks.Task SaveAllAsync()
    {
        if (SaveBehavior == SaveBehavior.NoSaving)
        {
            StatusText = UiStrings.SavingDisabled;
            return;
        }

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

                await doc.SaveCommand.Execute();
            }
        }
        StatusText = UiStrings.AllDocumentsSaved;
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
        StatusText = string.Format(UiStrings.SymbolRenamed, (object?)(info.Name), (object?)(newName));
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
        StatusText = string.Format(UiStrings.ActionApplied, (object?)(selection.Title));
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

    private async System.Threading.Tasks.Task ApplyWorkspaceEditAsync(LanguageWorkspaceEdit edit)
    {
        foreach (LanguageDocumentEdit docEdit in edit.DocumentEdits)
        {
            if (string.IsNullOrWhiteSpace(docEdit.FilePath))
            {
                continue;
            }

            TextDocumentViewModel? openText = Documents
                .OfType<TextDocumentViewModel>()
                .FirstOrDefault(doc =>
                    FileSystemPathComparison.Equals(doc.FilePath, docEdit.FilePath));

            if (openText is not null)
            {
                openText.ApplyTextEdits(docEdit.Edits);
                continue;
            }

            if (!System.IO.File.Exists(docEdit.FilePath))
            {
                continue;
            }

            string text = await System.IO.File.ReadAllTextAsync(docEdit.FilePath);
            string updated = ApplyEditsToText(text, docEdit.Edits);
            if (!string.Equals(text, updated, StringComparison.Ordinal))
            {
                await System.IO.File.WriteAllTextAsync(docEdit.FilePath, updated);
            }
        }
    }

    private static string ApplyEditsToText(string text, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0)
        {
            return text;
        }

        StringBuilder builder = new(text);
        foreach (TextEdit edit in edits.OrderByDescending(e => e.Offset))
        {
            int offset = Math.Clamp(edit.Offset, 0, builder.Length);
            int length = Math.Clamp(edit.Length, 0, builder.Length - offset);
            builder.Remove(offset, length);
            builder.Insert(offset, edit.NewText ?? string.Empty);
        }

        return builder.ToString();
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
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(evt =>
            {
                _ = AutoSaveAsync(doc);
            });

        _autoSaveSubscriptions[doc] = subscription;
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
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(evt =>
            {
                _ = AutoSaveAsync(doc);
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

        await doc.SaveCommand.Execute();
        StatusText = string.Format(UiStrings.DocumentAutoSaved, (object?)(doc.FileName));
    }

    private async System.Threading.Tasks.Task AutoSaveAsync(TextDocumentViewModel doc)
    {
        if (SaveBehavior != SaveBehavior.AutoSave)
        {
            return;
        }

        if (!doc.IsModified)
        {
            return;
        }

        await doc.SaveCommand.Execute();
        StatusText = string.Format(UiStrings.DocumentAutoSaved, (object?)(doc.FileName));
    }

    public bool TryGetExtensionView(string viewId, out ExtensionViewModel? viewModel)
    {
        return _extensionViews.TryGetValue(viewId, out viewModel);
    }

    private async System.Threading.Tasks.Task ShowCommandPaletteAsync()
    {
        if (CommandPaletteItems.Count == 0)
        {
            StatusText = UiStrings.NoExtensionCommands;
            return;
        }

        CommandPaletteRequest request = new(UiStrings.CommandPalette, CommandPaletteItems.ToList());
        ExtensionCommandPaletteItemViewModel? selected = await CommandPaletteInteraction.Handle(request);
        if (selected is null)
        {
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

        StatusText = string.Format(UiStrings.ExtensionCommandUnavailable, (object?)(commandId));
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
            SetPersistentStatus(string.Format(UiStrings.ExtensionCommandFailed, (object?)(ex.Message)));
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
                ResolveTreeProvider(contribution.ViewId)),
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
                _ => ExtensionMenuItems
            };

            target.Add(new ExtensionMenuItemViewModel(
                item.CommandId,
                item.Title,
                location,
                item.Group,
                item.Priority,
                CreateExtensionCommand(item.CommandId),
                GetExtensionCommandInputGesture(item.CommandId)));
        }

        GroupViewMenuItems();
        UpdateFileNewMenuEntries();
    }

    private static readonly string[] ViewMenuGroupOrder = ["views.debug", "views.bottom", "views.left", "views.right"];

    // The panels menu lists the debugger tools first and separates each group from the next; groups without a
    // known place follow alphabetically.
    private void GroupViewMenuItems()
    {
        List<IGrouping<string, ExtensionMenuItemViewModel>> groups = ViewMenuItems
            .GroupBy(item => item.Group ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => Array.IndexOf(ViewMenuGroupOrder, group.Key) is int index and >= 0 ? index : ViewMenuGroupOrder.Length)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ViewMenuItems.Clear();
        foreach (IGrouping<string, ExtensionMenuItemViewModel> group in groups)
        {
            if (ViewMenuItems.Count > 0)
            {
                ViewMenuItems.Add(new ExtensionMenuItemViewModel(
                    "view.separator." + group.Key, "-", ExtensionMenuLocations.View, group.Key, int.MinValue,
                    ReactiveCommand.Create(() => { })));
            }

            foreach (ExtensionMenuItemViewModel item in group)
            {
                ViewMenuItems.Add(item);
            }
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
                CreateExtensionCommand(entry.Key)));
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

    private void PruneExtensionCommandCache()
    {
        HashSet<string> activeCommandIds = new(StringComparer.Ordinal);

        foreach (ExtensionMenuItemViewModel item in ExtensionMenuItems)
        {
            activeCommandIds.Add(item.CommandId);
        }

        foreach (ExtensionMenuItemViewModel item in FileMenuItems)
        {
            activeCommandIds.Add(item.CommandId);
        }

        foreach (ExtensionMenuItemViewModel item in FileNewMenuItems)
        {
            activeCommandIds.Add(item.CommandId);
        }

        foreach (ExtensionMenuItemViewModel item in ViewMenuItems)
        {
            activeCommandIds.Add(item.CommandId);
        }

        foreach (ExtensionMenuItemViewModel item in ServerMenuItems)
        {
            activeCommandIds.Add(item.CommandId);
        }

        foreach (ExtensionMenuItemViewModel item in DebugMenuItems)
        {
            activeCommandIds.Add(item.CommandId);
        }

        foreach (ExtensionMenuItemViewModel item in EditMenuItems)
        {
            activeCommandIds.Add(item.CommandId);
        }

        foreach (ExtensionMenuItemViewModel item in WorkspaceMenuItems)
        {
            activeCommandIds.Add(item.CommandId);
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
        if (_commandMetadata is not null && _commandMetadata.TryGet(commandId, out CommandMetadata metadata))
        {
            if (!EvaluateWhenExpressionForContext(metadata.When))
            {
                return false;
            }
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
                return false;
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
        IsToolboxVisible = true;
        IsPropertiesVisible = true;
        IsVisualTreeVisible = true;
        IsLogicalTreeVisible = true;
        IsOutputVisible = true;
        IsCollaborationVisible = false;
        IsAnimationEditorVisible = true;
        IsBreakpointsVisible = true;
        IsCallStackVisible = true;
        IsLocalsVisible = true;
        IsWatchesVisible = true;

        IRootDock layout = DockFactory.CreateDefaultLayout();
        InstallLayout(layout);

        DockFactory.CollapseBottomToolDock(layout);
        DockFactory.SaveLayout(layout);
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
    /// Replaces the active dock layout and re-attaches everything that is not part of a serialized
    /// layout: extension panels, open documents and running terminals.
    /// </summary>
    private void InstallLayout(IRootDock layout)
    {
        XamlEditorDockFactory.EnsureLayoutDefaults(layout);
        DockFactory.InitLayout(layout);
        DockFactory.EnsureOwnerReferences(layout);
        DockFactory.ConfigureToolViewModels(layout);
        DockFactory.ConfigureDocumentViewModels(layout);
        DockLayout = layout;
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
    /// Applies one of the ready-made layouts: starts from the default layout and hides every panel
    /// that the preset does not list.
    /// </summary>
    public void ApplyLayoutPreset(string presetId)
    {
        LayoutPreset? preset = null;
        foreach (LayoutPreset candidate in _layoutPresets)
        {
            if (string.Equals(candidate.Id, presetId, StringComparison.OrdinalIgnoreCase))
            {
                preset = candidate;
                break;
            }
        }

        if (preset is null)
        {
            return;
        }

        IRootDock layout = DockFactory.CreateDefaultLayout();
        InstallLayout(layout);

        foreach (IDockable tool in XamlEditorDockFactory.FindDockables<Tool>(layout))
        {
            string? panelId = tool switch
            {
                ExtensionTool extensionTool => extensionTool.ViewId,
                TerminalTool => null,
                _ => tool.Id
            };

            if (panelId is null)
            {
                continue;
            }

            if (!preset.IsVisible(panelId))
            {
                DockFactory.HideDockable(tool);
            }
            else if (preset.ExpandPinnedPanels && DockFactory.IsDockablePinned(tool, layout))
            {
                DockFactory.UnpinDockable(tool);
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

        DockFactory.SaveLayout(layout);
        SetCurrentLayout(null, isLoaded: true);
        StatusText = string.Format(UiStrings.LayoutLoaded, preset.Title);
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

        InstallLayout(layout);
        SyncPanelVisibilityFlagsFromLayout();
        DockFactory.SaveLayout(layout);
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

        DockFactory.SaveLayout(DockLayout, _userLayoutStore.GetPath(name));
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
            // Exited kommt vom Lesethread der PTY-Sitzung.
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
            ? string.Format(UiStrings.DebugPausedReason, (object?)(stopped.Reason))
            : string.Format(UiStrings.DebugPausedDescription, (object?)(stopped.Reason), (object?)(stopped.Description)));

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
            StatusText = string.Format(UiStrings.DebugServiceMissing, (object?)(serviceName));
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
            SetPersistentStatus(string.Format(UiStrings.DebugStartFailed, (object?)(ex.Message)));
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
            SetPersistentStatus(string.Format(UiStrings.DebuggerDownloadFailed, (object?)(ex.Message)));
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
            return;
        }

        bool changed = previous is null
                       || !FileSystemPathComparison.Equals(previous.ProjectPath, project.ProjectPath)
                       || !string.Equals(previous.TargetFramework, project.TargetFramework, StringComparison.OrdinalIgnoreCase);

        ActiveProject = project;
        ActiveProjectPath = project.ProjectPath;
        if (changed && !IsWorkspaceLoading) RefreshControlCatalog();
        this.RaisePropertyChanged(nameof(ActiveProjectName));
        StatusText = string.Format(UiStrings.StartupProjectStatus, (object?)(project.Name));
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
            SetPersistentStatus(string.Format(UiStrings.DebugStopFailed, (object?)(ex.Message)));
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
            SetPersistentStatus(string.Format(UiStrings.RunningProject, (object?)(project.Name)));
            LogOutput("Info", $"Run command: dotnet {args}");
        }
        catch (Exception ex)
        {
            SetPersistentStatus(string.Format(UiStrings.RunFailed, (object?)(ex.Message)));
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

        if (dockDoc is not null)
        {
            _dockDocuments[document.FilePath] = dockDoc;
            UpdateDockTitle(document, dockDoc);
            SubscribeDockTitleUpdates(document, dockDoc);
        }
    }

    private void UpdateDockTitle(IEditorDocumentViewModel document, IDockable dockDoc)
    {
        dockDoc.Title = GetDockTitle(document);
    }

    private string GetDockTitle(IEditorDocumentViewModel document)
    {
        return document switch
        {
            DesignerDocumentViewModel designer => designer.Title,
            TextDocumentViewModel text => text.IsModified ? $"{text.FileName}*" : text.FileName,
            _ => document.IsModified ? $"{document.FileName}*" : document.FileName
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
                        e.EventArgs.PropertyName == nameof(IEditorDocumentViewModel.IsModified))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
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

        IDockable? dockable = XamlEditorDockFactory.FindDockable<IDockable>(DockLayout, id);
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
            CloseDockDocument(doc);
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
            _previewerLaunchService.StopPreviewer(designer.FilePath);
        }
        doc.Dispose();
        StatusText = string.Format(UiStrings.DocumentClosed, (object?)(doc.FileName));
    }

    /// <summary>
    /// Opens a specific file.
    /// </summary>
    public System.Threading.Tasks.Task OpenFileAsync(string filePath)
    {
        return OpenFileAsync(filePath, allowWorkspaceLoad: true);
    }

    internal System.Threading.Tasks.Task OpenFileAsync(string filePath, bool allowWorkspaceLoad)
    {
        return EnsureDocumentOpenAsync(filePath, addRecent: true, updateStatus: true, allowWorkspaceLoad: allowWorkspaceLoad);
    }

    private async Task CloseWorkspaceAsync()
    {
        IsWorkspaceLoading = true;
        try
        {
            foreach (IEditorDocumentViewModel document in Documents.ToArray()) CloseDocument(document);
            _workspace = null;
            _workspacePath = null;
            HasWorkspace = false;
            _workspaceInfoUpdater?.UpdateWorkspacePath(null);
            _controlCatalogUpdater?.SetWorkspaceAssemblies(Array.Empty<string>());
            WorkspaceProjects.Clear();
            _projectLookup.Clear();
            SetActiveProject(null);
            SolutionExplorer.Clear();
            SolutionExplorer.IsVisible = false;
            _assemblyResolver?.Dispose();
            _assemblyResolver = null;
            WorkspaceDesignThemeRegistry.Clear();
            _backNavigation.Clear();
            _forwardNavigation.Clear();
            UpdateNavigationState();

            if (_languageRegistry is not null)
            {
                foreach (ILanguageIntellisenseService service in _languageRegistry.Services)
                    await service.ClearWorkspaceAsync();
            }
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

    public System.Threading.Tasks.Task OpenFolderAsync(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !System.IO.Directory.Exists(folderPath))
        {
            RemoveRecentFile(folderPath, RecentEntryKind.Folder);
            StatusText = UiStrings.FolderNotFound;
            return System.Threading.Tasks.Task.CompletedTask;
        }

        StopDocumentPreviewers();
        _workspace = null;
        _controlCatalogUpdater?.SetWorkspaceAssemblies(Array.Empty<string>());
        _workspacePath = folderPath;
        _workspaceInfoUpdater?.UpdateWorkspacePath(_workspacePath);
        HasWorkspace = false;

        WorkspaceProjects.Clear();
        _projectLookup.Clear();
        SetActiveProject(null);
        SolutionExplorer.Clear();
        SolutionExplorer.IsVisible = false;

        string name = System.IO.Path.GetFileName(folderPath.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));
        StatusText = string.Format(UiStrings.FolderOpened, (object?)(name));
        LogOutput("Info", $"Opened folder: {folderPath}");
        AddRecentFile(System.IO.Path.TrimEndingDirectorySeparator(folderPath), RecentEntryKind.Folder);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task OpenFromSolutionExplorerAsync(string filePath)
    {
        await EnsureDocumentOpenAsync(filePath, addRecent: true, updateStatus: true, allowWorkspaceLoad: false);
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
            await EnsureDocumentOpenAsync(path, addRecent: true, updateStatus: false, allowWorkspaceLoad: false);
        }

        StatusText = string.Format(UiStrings.FilesOpened, (object?)(paths.Count));
    }

    private async System.Threading.Tasks.Task<IEditorDocumentViewModel?> EnsureDocumentOpenAsync(
        string filePath,
        bool addRecent,
        bool updateStatus,
        bool allowWorkspaceLoad)
    {
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

        IEditorDocumentViewModel? existing = Documents.FirstOrDefault(d => d.FilePath == filePath);
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
            await designer.LoadAsync();
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
            await textDoc.LoadAsync();
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
                    // Die vorhandene Historie enthält nur Pfade, daher einmalig anhand des Ziels zuordnen.
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
        SetPersistentStatus(string.Format(UiStrings.WorkspaceLoadingStatus, (object?)(workspaceName)));
        LogOutput("Info", $"Loading workspace: {workspacePath}");
        IsWorkspaceLoading = true;

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
                SetPersistentStatus(string.Format(UiStrings.WorkspaceLoadFailed, (object?)(ex.Message)));
                HasWorkspace = false;
                return;
            }

            StopDocumentPreviewers();
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
            bool usesAssemblySnapshots = workspace.Projects.Any(project => project.AssemblySnapshotDirectory is not null);
            if ((!hasAnyProjectOutputs || hasMissingProjectOutputs) && !usesAssemblySnapshots)
            {
                await RunDotNetCommandAsync(workspacePath, "restore", cancellationToken: cancellationToken);
                await RunDotNetCommandAsync(workspacePath, "build", cancellationToken: cancellationToken);
                assemblySet = CollectWorkspaceAssemblies(
                    workspace,
                    out hasAnyProjectOutputs,
                    out hasMissingProjectOutputs);
            }

            LogAssemblySet(assemblySet, hasAnyProjectOutputs, hasMissingProjectOutputs);
            if (usesAssemblySnapshots && hasMissingProjectOutputs)
                LogOutput("Warning", UiStrings.SnapshotOutputsMissing);

            if (assemblySet.All.Count > 0)
            {
                SetPersistentStatus(string.Format(UiStrings.WorkspaceAssembliesLoading, (object?)(workspaceName)));
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

            StatusText = string.Format(UiStrings.WorkspaceLoadedStatus, (object?)(name));
            LogOutput("Info", $"Loaded workspace: {name}");
            StartLanguageWorkspaceWarmup();
        }
        finally
        {
            IsWorkspaceLoading = false;
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
            SetPersistentStatus(string.Format(UiStrings.DotnetCommandFailed, (object?)(displayCommand)));
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
        return DebugToolConsentInteraction.Handle(request).ToTask();
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
            await _previewerLaunchService.SendUpdateXamlAsync(document.FilePath, xaml, workspace,
                (level, message) => LogOutput(level, message), workspacePath);
            if (!document.IsDisposed && ReferenceEquals(workspace, _workspace)) document.SetPreviewerLaunchError(null);
        }
        catch (Exception) when (document.IsDisposed || !Documents.Contains(document) || !ReferenceEquals(workspace, _workspace))
        {
            // Closing a document or replacing its workspace invalidates pending transport work.
        }
    }

    private static string? GetPreviewerXaml(DesignerDocumentViewModel document)
    {
        if (document.MetadataService is { } metadata && document.SyncEngine.CanEditAst
            && document.SyncEngine.CurrentDocument is { Root: not null } ast)
            return new XamlVisualEditor.Designer.Rendering.RuntimeDesignRenderer(metadata)
                .PrepareXaml(ast, themeVariant: document.DesignSurface.PreviewThemeVariant, themeRequest: document.PreviewThemeRequest);
        return document.SyncEngine.CurrentText;
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
        ConfigurePreviewThemes(document);
        document.SetPreviewerLaunchError(null);
        if (workspace is null)
        {
            document.SetPreviewerLaunchError(UiStrings.PreviewerNeedsProject);
            return;
        }

        if (!await EnsurePreviewerTrustAsync(document.FilePath))
        {
            if (ReferenceEquals(ActiveDesignerDocument, document)) StatusText = UiStrings.PreviewerStartCanceled;
            return;
        }

        if (document.IsDisposed || !Documents.Contains(document) || !ReferenceEquals(_workspace, workspace)) return;
        string? xamlText = GetPreviewerXaml(document);
        PreviewerLaunchResult result = await _previewerLaunchService.StartPreviewerAsync(
            document.FilePath,
            xamlText,
            workspace,
            workspacePath,
            command => ReferenceEquals(_workspace, workspace)
                ? RunWorkspaceCommandAsync(command) : System.Threading.Tasks.Task.CompletedTask,
            (level, message) => LogOutput(level, message));

        if (document.IsDisposed || !Documents.Contains(document) || !ReferenceEquals(_workspace, workspace))
        {
            if (result.Process is not null) _previewerLaunchService.StopPreviewer(document.FilePath, result.Process);
            return;
        }

        if (result.Success)
        {
            if (ReferenceEquals(ActiveDesignerDocument, document)) StatusText = UiStrings.PreviewerStarted;
            if (_previewerLaunchService.TryGetSession(document.FilePath, out PreviewerTcpSession? session))
            {
                document.PreviewerSession = session;
            }
            return;
        }

        if (ReferenceEquals(ActiveDesignerDocument, document)) SetPersistentStatus(UiStrings.PreviewerStartFailed);
        document.PreviewerSession = null;
        document.SetPreviewerLaunchError(result.ErrorMessage ?? UiStrings.PreviewerStartFailedText);
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            LogOutput("Error", result.ErrorMessage);
        }
    }

    private async System.Threading.Tasks.Task<bool> EnsurePreviewerTrustAsync(string xamlFilePath)
    {
        // Kopflose Bridge-Laeufe koennen den Trust-Dialog nicht bedienen; der Schalter gewaehrt
        // den Start analog zu XVE_IDEBRIDGE_AUTOCONSENT.
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
    /// Startet den Previewer fuer eine geoeffnete XAML-Datei, wartet auf einen stabilen Frame
    /// und speichert ihn als PNG. Der Previewer-Prozess wird danach beendet, damit sich bei
    /// Serienlaeufen keine Host-Prozesse ansammeln.
    /// </summary>
    public async System.Threading.Tasks.Task<XamlVisualEditor.Extensions.PreviewExportResult> ExportPreviewAsync(
        string filePath,
        string outputPath,
        int timeoutMs)
    {
        DesignerDocumentViewModel? doc = Documents
            .OfType<DesignerDocumentViewModel>()
            .FirstOrDefault(d => FileSystemPathComparison.Equals(d.FilePath, filePath));
        if (doc is null)
        {
            return new(false, UiStrings.DesignerDocumentNotOpen, 0, 0, outputPath);
        }

        if (_workspace is null)
        {
            return new(false, UiStrings.NoWorkspaceLoaded, 0, 0, outputPath);
        }

        if (!await EnsurePreviewerTrustAsync(filePath))
        {
            return new(false, UiStrings.PreviewerTrustDenied, 0, 0, outputPath);
        }

        ActiveDocument = doc;
        PreviewerLaunchResult launch = await _previewerLaunchService.StartPreviewerAsync(
            filePath,
            doc.SyncEngine.CurrentText,
            _workspace,
            _workspacePath,
            command => RunWorkspaceCommandAsync(command),
            (level, message) => LogOutput(level, message));
        if (!launch.Success)
        {
            return new(false, launch.ErrorMessage ?? UiStrings.PreviewerStartFailedText, 0, 0, outputPath);
        }

        if (!_previewerLaunchService.TryGetSession(filePath, out PreviewerTcpSession? session) || session is null)
        {
            return new(false, UiStrings.NoPreviewerSession, 0, 0, outputPath);
        }

        doc.PreviewerSession = session;
        try
        {
            Avalonia.Remote.Protocol.Viewport.FrameMessage? frame = await WaitForStableFrameAsync(session, timeoutMs);
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
            doc.PreviewerSession = null;
            _previewerLaunchService.StopPreviewer(filePath);
        }
    }

    // Frames treffen waehrend des Aufbaus mehrfach ein (Viewport-Verhandlung); als stabil gilt
    // der letzte Frame, wenn 1,5 s lang weder Groessenwechsel noch Resize-Anforderung kamen.
    internal static async System.Threading.Tasks.Task<Avalonia.Remote.Protocol.Viewport.FrameMessage?> WaitForStableFrameAsync(
        PreviewerTcpSession session,
        int timeoutMs)
    {
        object gate = new();
        Avalonia.Remote.Protocol.Viewport.FrameMessage? last = session.LastFrame;
        DateTime lastChangeUtc = DateTime.UtcNow;

        void OnFrame(Avalonia.Remote.Protocol.Viewport.FrameMessage frame)
        {
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
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "XamlVisualEditor");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "previewer-trust.json");
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

    private void OnPreviewerErrorReceived(PreviewerErrorInfo error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (string.IsNullOrWhiteSpace(error.FilePath))
            {
                LogOutput("Error", error.Message);
                return;
            }

            int line = error.Line is > 0 ? error.Line.Value : 1;
            int column = error.Column is > 0 ? error.Column.Value : 1;
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
        SetPersistentStatus(string.Format(UiStrings.DotnetCommandRunning, (object?)(displayCommand)));
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
            StatusText = string.Format(UiStrings.DotnetCommandCompleted, (object?)(displayCommand));
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
        string? appXamlPath = FindApplicationXamlPath(workspace);
        if (appXamlPath is null)
        {
            WorkspaceDesignThemeRegistry.Clear();
            LogOutput("Info", "Design themes: no App.axaml/App.xaml found in workspace");
            return;
        }

        try
        {
            IReadOnlyList<string> details = WorkspaceDesignThemeLoader.LoadFromApplicationXaml(appXamlPath);
            LogOutput("Info", $"Design themes from {appXamlPath}: {string.Join("; ", details)}");
        }
        catch (Exception ex)
        {
            WorkspaceDesignThemeRegistry.Clear();
            LogOutput("Warning", $"Design themes failed for {appXamlPath}: {ex.Message}");
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
            if (outputs.Count > 0 || project.AssemblySnapshotDirectory is not null)
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
            ? string.Format(UiStrings.ProjectOutputsCount, (object?)(preferredCount))
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
        DockFactory.DockableClosed -= OnDockableClosed;

        if (DockLayout is not null)
        {
            DockFactory.SaveLayout(DockLayout);
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
                this.RaisePropertyChanged(nameof(IsRunnableButNotStartup));
            }
        }
    }

    /// <summary>Gets whether this project is runnable.</summary>
    public bool IsRunnableProject => Project?.IsExecutable == true;

    /// <summary>Gets whether this project is runnable but not selected as startup.</summary>
    public bool IsRunnableButNotStartup => IsRunnableProject && !IsStartupProject;

    /// <summary>Raised when a file node is double-clicked (opened).</summary>
    public event Action<string>? FileOpened;

    /// <summary>
    /// Command to open the associated file.
    /// </summary>
    public ReactiveCommand<Unit, Unit>? OpenCommand { get; }

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

            // References folder
            if (project.References.Count > 0)
            {
                object referencesFolderIcon = GetIconWithFallback(systemIcons, null, isDirectory: true, fallbackIcon: "📚");
                SolutionExplorerNodeViewModel refsFolder = new("References", referencesFolderIcon, SolutionExplorerNodeKind.Folder);

                foreach (AssemblyReference asmRef in project.References)
                {
                    object referenceIcon = GetIconWithFallback(systemIcons, asmRef.Path, isDirectory: false, fallbackIcon: "🔗");
                    SolutionExplorerNodeViewModel refNode = new(
                        asmRef.Name, referenceIcon, SolutionExplorerNodeKind.Reference, asmRef.Path);
                    refsFolder.Children.Add(refNode);
                }

                projectNode.Children.Add(refsFolder);
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
            if (parts.Length == 0)
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

    public ReactiveCommand<Unit, Unit> OpenSelectedCommand { get; }

    public ReactiveCommand<Unit, Unit> SetStartupProjectCommand { get; }

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
            VirtualizeChildren = true
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
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplyFilterAndSearch);

        IObservable<bool> canOpen = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => node is not null
                            && (node.Kind == SolutionExplorerNodeKind.File || node.Kind == SolutionExplorerNodeKind.XamlFile)
                            && !string.IsNullOrWhiteSpace(node.FullPath));
        IObservable<bool> canSetStartup = this.WhenAnyValue(x => x.SelectedNode)
            .Select(node => _extensionCommands is not null
                            && node?.Kind == SolutionExplorerNodeKind.Project
                            && node.Project is not null);

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
        Root = SolutionExplorerNodeViewModel.FromWorkspace(workspace, solutionName, _systemIcons);
        CollapseAll(Root);
        WireFileOpen(Root);
        SetRoot(Root);
    }

    public void Clear()
    {
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
        HashSet<object> matches = BuildMatchSet(RootItems, query);
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
