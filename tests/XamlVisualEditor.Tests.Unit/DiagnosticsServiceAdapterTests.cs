using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Xaml.Ast;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DiagnosticsServiceAdapterTests
{
    private const string EmptyUserControl = "<UserControl xmlns=\"https://github.com/avaloniaui\" />";

    [AvaloniaFact(Timeout = 30000)]
    public async Task GetDiagnosticsAsync_MapsDesignerDocumentDiagnostics()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        using DesignerDocumentViewModel designer = new("AdapterDesigner.axaml");
        await designer.SyncEngine.LoadAsync(EmptyUserControl).WaitAsync(cancellationToken);
        await FlushAsync().WaitAsync(cancellationToken);
        designer.SetRuntimeRenderFailure(new InvalidOperationException("Broken element"));
        using MainWindowViewModel viewModel = new();
        viewModel.Documents.Add(designer);
        using DiagnosticsServiceAdapter adapter = new(viewModel);

        IReadOnlyList<LanguageDiagnostic> diagnostics =
            await adapter.GetDiagnosticsAsync(designer.FilePath, cancellationToken).WaitAsync(cancellationToken);

        LanguageDiagnostic diagnostic = Assert.Single(diagnostics, item => item.Source == "Designer");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Broken element", diagnostic.Message);
        Assert.Equal(designer.FilePath, diagnostic.FilePath);
        MutableAstObjectNode root = designer.SyncEngine.CurrentDocument!.Root!;
        Assert.Equal(root.Line, diagnostic.Range.Start.Line);
        Assert.Equal(root.Column, diagnostic.Range.Start.Column);
        Assert.Equal(root.Column + 1, diagnostic.Range.End.Column);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task DesignerDiagnosticsChange_PublishesSnapshot()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        using DesignerDocumentViewModel designer = new("AdapterSnapshot.axaml");
        await designer.SyncEngine.LoadAsync(EmptyUserControl).WaitAsync(cancellationToken);
        await FlushAsync().WaitAsync(cancellationToken);
        using MainWindowViewModel viewModel = new();
        viewModel.Documents.Add(designer);
        using DiagnosticsServiceAdapter adapter = new(viewModel);
        List<DiagnosticsSnapshotPublishedEventArgs> published = new();
        adapter.DiagnosticsSnapshotPublished += (_, args) => published.Add(args);

        designer.SetRuntimeRenderFailure(new InvalidOperationException("Late failure"));

        DiagnosticsDocumentSnapshot snapshot = Assert.Single(published.Last().Snapshots,
            item => string.Equals(item.FilePath, designer.FilePath, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Diagnostics, item => item.Message == "Late failure");
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task GetDiagnosticsAsync_FindsOpenTextDocumentByPath()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        const string filePath = "AdapterText.xml";
        using TextDocumentViewModel text = new(filePath);
        text.Diagnostics.Add(new LanguageDiagnostic
        {
            Severity = DiagnosticSeverity.Warning,
            Message = "Suspicious attribute",
            FilePath = filePath,
            Range = new LanguageTextRange(new LanguageTextPosition(1, 2), new LanguageTextPosition(1, 5))
        });
        using MainWindowViewModel viewModel = new();
        viewModel.Documents.Add(text);
        using DiagnosticsServiceAdapter adapter = new(viewModel);

        IReadOnlyList<LanguageDiagnostic> diagnostics =
            await adapter.GetDiagnosticsAsync(filePath, cancellationToken).WaitAsync(cancellationToken);

        LanguageDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("Suspicious attribute", diagnostic.Message);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task GetDiagnosticsAsync_ReturnsNothingForUnknownPath()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        using MainWindowViewModel viewModel = new();
        using DiagnosticsServiceAdapter adapter = new(viewModel);

        IReadOnlyList<LanguageDiagnostic> diagnostics =
            await adapter.GetDiagnosticsAsync("NotOpen.axaml", cancellationToken).WaitAsync(cancellationToken);

        Assert.Empty(diagnostics);
    }

    private static async Task FlushAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}
