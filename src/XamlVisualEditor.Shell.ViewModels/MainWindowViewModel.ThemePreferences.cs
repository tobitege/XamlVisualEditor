using System.Collections.Specialized;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<DesignerDocumentViewModel, IDisposable> _themePreferenceSubscriptions = new();
    private IWorkspaceInfo? _themeWorkspaceInfo;
    private IExtensionStorage? _themePreferenceStorage;
    private DesignerThemeSelection? _workspacePreviewTheme;
    private string _themePreferenceKey = GetPreviewThemePreferenceKey(null);
    private int _themePreferenceLoadVersion;
    private int _themePreferenceEditVersion;
    private bool _applyingWorkspaceTheme;
    private bool _themePreferencesDisposed;
    internal Task ThemePreferencesLoaded { get; private set; } = Task.CompletedTask;
    internal Task ThemePreferencesSaved { get; private set; } = Task.CompletedTask;

    internal static string GetPreviewThemePreferenceKey(string? workspacePath)
    {
        string identity = string.IsNullOrWhiteSpace(workspacePath) ? "no-workspace"
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        return "designer.previewTheme:" + identity;
    }

    private void InitializeThemePreferences(IWorkspaceInfo? workspaceInfo, IExtensionStorage? storage)
    {
        _themeWorkspaceInfo = workspaceInfo;
        _themePreferenceStorage = storage;
        Documents.CollectionChanged += OnThemePreferenceDocumentsChanged;
        if (workspaceInfo is not null) workspaceInfo.WorkspaceChanged += OnThemePreferenceWorkspaceChanged;
        _disposables.Add(Disposable.Create(() =>
        {
            _themePreferencesDisposed = true;
            Interlocked.Increment(ref _themePreferenceLoadVersion);
            Documents.CollectionChanged -= OnThemePreferenceDocumentsChanged;
            if (_themeWorkspaceInfo is not null) _themeWorkspaceInfo.WorkspaceChanged -= OnThemePreferenceWorkspaceChanged;
            foreach (IDisposable subscription in _themePreferenceSubscriptions.Values) subscription.Dispose();
            _themePreferenceSubscriptions.Clear();
            if (!ThemePreferencesSaved.Wait(TimeSpan.FromSeconds(5)))
                LogOutput("Warning", "Saving the workspace preview theme did not finish before shutdown.");
        }));
        RefreshThemePreferenceDocuments();
        ThemePreferencesLoaded = LoadThemePreferencesAsync(workspaceInfo?.WorkspacePath);
    }

    private void OnThemePreferenceWorkspaceChanged(object? sender, WorkspaceChangedEventArgs args) =>
        ThemePreferencesLoaded = LoadThemePreferencesAsync(args.WorkspacePath);

    private void OnThemePreferenceDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        RefreshThemePreferenceDocuments();

    private void RefreshThemePreferenceDocuments()
    {
        if (_themePreferencesDisposed) return;
        foreach (DesignerDocumentViewModel removed in _themePreferenceSubscriptions.Keys.Where(document => !Documents.Contains(document)).ToArray())
        {
            _themePreferenceSubscriptions[removed].Dispose();
            _themePreferenceSubscriptions.Remove(removed);
        }
        foreach (DesignerDocumentViewModel document in Documents.OfType<DesignerDocumentViewModel>())
        {
            if (_themePreferenceSubscriptions.ContainsKey(document)) continue;
            ConfigurePreviewThemes(document);
            document.SetPreviewThemeSelection(_workspacePreviewTheme);
            _themePreferenceSubscriptions.Add(document, document.WhenAnyValue(item => item.PreviewThemeRequest)
                .Select(request => request?.Selection).DistinctUntilChanged().Skip(1).Subscribe(OnWorkspacePreviewThemeSelected));
        }
    }

    private async Task LoadThemePreferencesAsync(string? workspacePath)
    {
        int version = Interlocked.Increment(ref _themePreferenceLoadVersion);
        try
        {
            string key = GetPreviewThemePreferenceKey(workspacePath);
            int editVersion = 0;
            await OnThemePreferenceUiAsync(() =>
            {
                if (!IsCurrentThemePreferenceLoad(version)) return;
                _themePreferenceKey = key;
                editVersion = _themePreferenceEditVersion;
                ApplyWorkspacePreviewTheme(null);
            }).ConfigureAwait(false);
            if (!IsCurrentThemePreferenceLoad(version)) return;
            DesignerThemeSelection? selection = _themePreferenceStorage is null ? null
                : await _themePreferenceStorage.GetAsync<DesignerThemeSelection>(key, CancellationToken.None).ConfigureAwait(false);
            await OnThemePreferenceUiAsync(() =>
            {
                if (IsCurrentThemePreferenceLoad(version) && editVersion == _themePreferenceEditVersion)
                    ApplyWorkspacePreviewTheme(selection);
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await OnThemePreferenceUiAsync(() =>
            {
                if (IsCurrentThemePreferenceLoad(version)) ReportThemePreferenceError(false, error);
            }).ConfigureAwait(false);
        }
    }

    private bool IsCurrentThemePreferenceLoad(int version) =>
        !_themePreferencesDisposed && version == Volatile.Read(ref _themePreferenceLoadVersion);

    private void ApplyWorkspacePreviewTheme(DesignerThemeSelection? selection)
    {
        _workspacePreviewTheme = selection;
        _applyingWorkspaceTheme = true;
        try
        {
            foreach (DesignerDocumentViewModel document in Documents.OfType<DesignerDocumentViewModel>())
                document.SetPreviewThemeSelection(selection);
        }
        finally { _applyingWorkspaceTheme = false; }
    }

    private void OnWorkspacePreviewThemeSelected(DesignerThemeSelection? selection)
    {
        if (_themePreferencesDisposed || _applyingWorkspaceTheme || selection == _workspacePreviewTheme) return;
        _themePreferenceEditVersion++;
        ApplyWorkspacePreviewTheme(selection);
        ThemePreferencesSaved = SaveThemePreferenceAsync(ThemePreferencesSaved, _themePreferenceKey, selection);
    }

    private async Task SaveThemePreferenceAsync(Task previous, string key, DesignerThemeSelection? selection)
    {
        try
        {
            await previous.ConfigureAwait(false);
            if (_themePreferenceStorage is not null)
                await _themePreferenceStorage.SetAsync(key, selection, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            RxSchedulers.MainThreadScheduler.Schedule(() =>
            {
                if (!_themePreferencesDisposed && key == _themePreferenceKey) ReportThemePreferenceError(true, error);
            });
        }
    }

    private void ReportThemePreferenceError(bool saving, Exception error)
    {
        string message = string.Format(saving
            ? XamlVisualEditor.Core.Localization.UiStrings.WorkspaceThemeSaveFailed
            : XamlVisualEditor.Core.Localization.UiStrings.WorkspaceThemeLoadFailed, error.Message);
        LogOutput("Warning", message);
        StatusText = message;
        foreach (DesignerDocumentViewModel document in Documents.OfType<DesignerDocumentViewModel>())
            document.DesignSurface.PreviewThemeStatus = message;
    }

    private static Task OnThemePreferenceUiAsync(Action action) =>
        Observable.Start(action, RxSchedulers.MainThreadScheduler).ToTask();
}
