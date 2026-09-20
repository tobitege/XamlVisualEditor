using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class DesignerDocumentViewModel
{
    private IReadOnlyList<LanguageDiagnostic> _diagnostics = Array.Empty<LanguageDiagnostic>();
    private IReadOnlyList<LanguageDiagnostic> _syntaxDiagnostics = Array.Empty<LanguageDiagnostic>();
    private IReadOnlyList<LanguageDiagnostic> _renderDiagnostics = Array.Empty<LanguageDiagnostic>();
    private IReadOnlyList<LanguageDiagnostic> _previewerDiagnostics = Array.Empty<LanguageDiagnostic>();
    private IReadOnlyList<LanguageDiagnostic> _previewerLaunchDiagnostics = Array.Empty<LanguageDiagnostic>();
    private IReadOnlyList<LanguageDiagnostic> _previewThemeDiagnostics = Array.Empty<LanguageDiagnostic>();
    private IReadOnlyList<LanguageDiagnostic> _interactionDiagnostics = Array.Empty<LanguageDiagnostic>();
    private string? _interactionDiagnosticsText;
    private string? _renderDiagnosticsText;

    public IReadOnlyList<LanguageDiagnostic> Diagnostics
    {
        get => _diagnostics;
        private set => this.RaiseAndSetIfChanged(ref _diagnostics, value);
    }

    private void InitializeDesignerDiagnostics()
    {
        _disposables.Add(this.WhenAnyValue(document => document.UseExternalPreviewer)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => { if (!IsDisposed) PublishDesignerDiagnostics(); }));

        _disposables.Add(this.WhenAnyValue(document => document.PreviewerSession)
            .Select(session => session is null
                ? Observable.Return<PreviewerErrorInfo?>(null)
                : Observable.Create<PreviewerErrorInfo?>(observer =>
                {
                    void OnErrorChanged(PreviewerErrorInfo? error) => observer.OnNext(error);
                    session.ErrorChanged += OnErrorChanged;
                    observer.OnNext(session.LastErrorInfo);
                    return Disposable.Create(() => session.ErrorChanged -= OnErrorChanged);
                }))
            .Switch()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(error =>
            {
                if (IsDisposed || !Equals(PreviewerSession?.LastErrorInfo, error)) return;
                _previewerDiagnostics = error is null
                    ? Array.Empty<LanguageDiagnostic>()
                    : new[] { CreateDesignerDiagnostic(error.Message, DiagnosticSeverity.Error,
                        "Previewer", "XVE_PREVIEW", error.Line ?? 1, error.Column ?? 1, 1) };
                PublishDesignerDiagnostics();
            }));

        _disposables.Add(SyncEngine.SyncEvents.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(change =>
        {
            if (IsDisposed || change.UpdatedText != SyncEngine.CurrentText) return;
            if (_interactionDiagnosticsText != change.UpdatedText)
                _interactionDiagnostics = Array.Empty<LanguageDiagnostic>();
            if (change.Diagnostics is { } syntaxDiagnostics)
                _syntaxDiagnostics = syntaxDiagnostics.Select(diagnostic => CreateDesignerDiagnostic(
                    diagnostic.Message, diagnostic.Severity, "XAML", "XVE_XAML",
                    diagnostic.Line, diagnostic.Column, diagnostic.Length)).ToArray();
            if (_renderDiagnosticsText != change.UpdatedText)
                _renderDiagnostics = Array.Empty<LanguageDiagnostic>();
            PublishDesignerDiagnostics();
        }));
    }

    public void SetRuntimeRenderDiagnostics(IReadOnlyList<DesignRenderDiagnostic> diagnostics)
    {
        if (IsDisposed) return;
        _renderDiagnosticsText = SyncEngine.CurrentText;
        _renderDiagnostics = diagnostics.Select(diagnostic =>
        {
            MutableAstNode? node = NodeMap.FindById(diagnostic.NodeId);
            return CreateDesignerDiagnostic($"{diagnostic.TypeName}: {diagnostic.Message}",
                DiagnosticSeverity.Error, "Designer", "XVE_RENDER_CONTROL", node?.Line ?? 1, node?.Column ?? 1, 1);
        }).ToArray();
        PublishDesignerDiagnostics();
    }

    public void SetRuntimeRenderFailure(Exception exception)
    {
        if (IsDisposed) return;
        _renderDiagnosticsText = SyncEngine.CurrentText;
        MutableAstObjectNode? root = SyncEngine.CurrentDocument?.Root;
        _renderDiagnostics = !SyncEngine.CanEditAst || string.IsNullOrWhiteSpace(SyncEngine.CurrentText)
            ? Array.Empty<LanguageDiagnostic>()
            : new[] { CreateDesignerDiagnostic(exception.Message, DiagnosticSeverity.Error,
                "Designer", "XVE_RENDER", root?.Line ?? 1, root?.Column ?? 1, 1) };
        PublishDesignerDiagnostics();
    }

    public void SetPreviewerLaunchError(string? error)
    {
        if (IsDisposed) return;
        _previewerLaunchDiagnostics = string.IsNullOrWhiteSpace(error) ? Array.Empty<LanguageDiagnostic>()
            : new[] { CreateDesignerDiagnostic(error, DiagnosticSeverity.Error, "Previewer", "XVE_PREVIEW_START", 1, 1, 1) };
        PublishDesignerDiagnostics();
    }

    public void SetDesignerInteractionError(Exception? error)
    {
        if (IsDisposed) return;
        _interactionDiagnosticsText = SyncEngine.CurrentText;
        MutableAstNode? node = SelectedNodeId is { } selected ? NodeMap.FindById(selected) : null;
        _interactionDiagnostics = error is null ? Array.Empty<LanguageDiagnostic>()
            : new[] { CreateDesignerDiagnostic(error.Message, DiagnosticSeverity.Error, "Designer", "XVE_EDIT",
                node?.Line ?? 1, node?.Column ?? 1, 1) };
        PublishDesignerDiagnostics();
    }

    private LanguageDiagnostic CreateDesignerDiagnostic(string message, DiagnosticSeverity severity,
        string source, string code, int line, int column, int length)
    {
        line = Math.Max(1, line);
        column = Math.Max(1, column);
        int endColumn = (int)Math.Min(int.MaxValue, (long)column + Math.Max(1, length));
        return new LanguageDiagnostic
        {
            FilePath = FilePath, Message = message, Severity = severity, Source = source, Code = code,
            Range = new LanguageTextRange(new LanguageTextPosition(line, column), new LanguageTextPosition(line, endColumn))
        };
    }

    private void PublishDesignerDiagnostics() =>
        Diagnostics = _syntaxDiagnostics
            .Concat(UseExternalPreviewer ? Array.Empty<LanguageDiagnostic>() : _renderDiagnostics)
            .Concat(_previewerLaunchDiagnostics).Concat(_previewerDiagnostics).Concat(_previewThemeDiagnostics).Concat(_interactionDiagnostics).ToArray();
}
