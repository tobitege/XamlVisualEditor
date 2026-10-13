using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using System.Xml;
using System.Xml.Linq;
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
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => { if (!IsDisposed) PublishDesignerDiagnostics(); }));

        _disposables.Add(Observable.Switch(this.WhenAnyValue(document => document.PreviewerSession)
            .Select(session => session is null
                ? Observable.Return<PreviewerErrorInfo?>(null)
                : Observable.Create<PreviewerErrorInfo?>(observer =>
                {
                    void OnErrorChanged(PreviewerErrorInfo? error) => observer.OnNext(error);
                    session.ErrorChanged += OnErrorChanged;
                    observer.OnNext(session.LastErrorInfo);
                    return Disposable.Create(() => session.ErrorChanged -= OnErrorChanged);
                }))
            )
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(error =>
            {
                if (IsDisposed || !Equals(PreviewerSession?.LastErrorInfo, error)) return;
                (int line, int column) = ResolvePreviewerErrorPosition(error, SyncEngine.CurrentText);
                _previewerDiagnostics = error is null
                    ? Array.Empty<LanguageDiagnostic>()
                    : new[] { CreateDesignerDiagnostic(error.Message, DiagnosticSeverity.Error,
                        "Previewer", "XVE_PREVIEW", line, column, 1) };
                PublishDesignerDiagnostics();
            }));

        _disposables.Add(SyncEngine.SyncEvents.WitnessOn(RxSchedulers.MainThreadScheduler).Subscribe(change =>
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

    internal static (int Line, int Column) ResolvePreviewerErrorPosition(PreviewerErrorInfo? error, string? sourceText)
    {
        if (sourceText is not null && error?.ElementPath is { Count: > 0 } path)
        {
            try
            {
                XContainer parent = XDocument.Parse(sourceText, LoadOptions.SetLineInfo);
                foreach (PreviewerElementPathSegment segment in path)
                {
                    XElement? element = parent.Elements(XName.Get(segment.Name, segment.Namespace))
                        .ElementAtOrDefault(segment.Index);
                    if (element is null) return (1, 1);
                    parent = element;
                }
                if (error.TextContent is { } text && parent is XElement owner
                    && owner.Nodes().OfType<XText>().FirstOrDefault(node => node.Value.Trim() == text) is { } textNode)
                    return GetTextStart(textNode);
                IXmlLineInfo location = (IXmlLineInfo)parent;
                return (location.LineNumber, location.LinePosition);
            }
            catch (XmlException) { }
        }
        return (error?.Line ?? 1, error?.Column ?? 1);
    }

    // A text node starts with the whitespace before its first character; the position names that character.
    private static (int Line, int Column) GetTextStart(XText node)
    {
        IXmlLineInfo location = node;
        int line = location.LineNumber;
        int column = location.LinePosition;
        foreach (char character in node.Value)
        {
            if (!char.IsWhiteSpace(character)) break;
            if (character == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
        return (line, column);
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
