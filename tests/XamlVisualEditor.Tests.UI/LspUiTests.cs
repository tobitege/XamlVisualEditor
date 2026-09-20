using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Threading;
using Xunit;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Language;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Intellisense;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.OutputExtension;

namespace XamlVisualEditor.Tests.UI;

public sealed class LspUiTests
{
    [AvaloniaFact]
    public async Task Diagnostics_Rendered_From_Language_Service()
    {
        string filePath = Path.Combine(Path.GetTempPath(), "XveLspTests", "Diagnostics.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        FakeLanguageService service = new("csharp")
        {
            Diagnostics = new[]
            {
                new LanguageDiagnostic
                {
                    FilePath = filePath,
                    Message = "Test error",
                    Severity = DiagnosticSeverity.Error,
                    Range = new LanguageTextRange(
                        new LanguageTextPosition(2, 9),
                        new LanguageTextPosition(2, 14))
                }
            }
        };

        ILanguageIntellisenseRegistry registry = new LanguageServiceRegistry(new[] { service });
        using MainWindowViewModel main = new(languageRegistry: registry);
        using EditorServicesAdapter editor = new(main);
        using DiagnosticsServiceAdapter diagnostics = new(main);
        using ProblemsPanelViewModel problems = new(diagnostics, editor);
        XamlVisualEditor.OutputExtension.Views.ProblemsPanelView panel = new() { DataContext = problems };
        DataGrid grid = panel.FindControl<DataGrid>("DiagnosticsGrid")!;
        DataGridColumn timeColumn = grid.Columns.Single(column => Equals(column.Header, "Since"));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(timeColumn.IsVisible);
        problems.ShowFirstSeen = true;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.True(timeColumn.IsVisible);
        EventHandler<DiagnosticsChannelsChangedEventArgs> channelsChanged = (_, args) => problems.HandleChannelsChanged(args.Channels);
        EventHandler<DiagnosticsSnapshotPublishedEventArgs> snapshotsChanged = (_, args) => problems.HandleSnapshotsPublished(args.Snapshots);
        diagnostics.ChannelsChanged += channelsChanged;
        diagnostics.DiagnosticsSnapshotPublished += snapshotsChanged;
        try
        {
            await File.WriteAllTextAsync(filePath, "class C {\n    int value;\n}");
            await main.OpenFileAsync(filePath);
            TextDocumentViewModel document = Assert.IsType<TextDocumentViewModel>(main.ActiveTextDocument);
            await WaitForConditionAsync(() => document.Diagnostics.Count > 0 && problems.Model.Flattened.Count > 0, TimeSpan.FromSeconds(3));

            LanguageDiagnostic? diag = document.DiagnosticColorizer.GetDiagnosticAt(2, 10);
            Assert.NotNull(diag);
            Assert.Equal(DiagnosticSeverity.Error, diag!.Severity);
            document.CaretOffset = document.Document.TextLength;
            Assert.True(await problems.NavigateToRelativeAsync(1, CancellationToken.None));
            Assert.Equal(filePath, main.ActiveTextDocument!.FilePath);
            Assert.Equal(document.GetOffsetForLineColumn(2, 9), document.CaretOffset);
            Assert.Equal("Test error", problems.SelectedDiagnostic!.Message);
            Assert.Same(problems.SelectedDiagnostic, grid.SelectedItem);
            Assert.Contains(problems.Model.Flattened.OfType<Avalonia.Controls.DataGridHierarchical.HierarchicalNode>(),
                row => ReferenceEquals(row.Item, grid.SelectedItem));

            service.Diagnostics = Array.Empty<LanguageDiagnostic>();
            document.Document.Text = "class C { }";
            await WaitForConditionAsync(() => document.Diagnostics.Count == 0 && problems.Model.Flattened.Count == 0, TimeSpan.FromSeconds(3));
            Assert.Null(problems.SelectedDiagnostic);
            Assert.False(await problems.NavigateToRelativeAsync(1, CancellationToken.None));
        }
        finally
        {
            diagnostics.ChannelsChanged -= channelsChanged;
            diagnostics.DiagnosticsSnapshotPublished -= snapshotsChanged;
        }
    }

    [AvaloniaFact]
    public async Task Completion_Window_Populates_From_Language_Service()
    {
        FakeLanguageService service = new("xml")
        {
            Completions = new[]
            {
                new CompletionItem { DisplayText = "Grid", InsertText = "Grid" }
            }
        };

        ILanguageIntellisenseRegistry registry = new LanguageServiceRegistry(new[] { service });
        XamlParsingService parser = new();
        XamlSerializationService serializer = new();
        AstNodeMap map = new();
        SyncEngine engine = new(parser, serializer, map);
        CompletionProviderRegistry completionRegistry = new();
        using XamlVisualEditor.CodeEditor.CodeEditorViewModel vm = new(
            "Test.xaml",
            engine,
            completionRegistry,
            registry);

        vm.Document.Text = "<";
        vm.CaretOffset = 1;
        vm.TriggerCompletionCommand.Execute().Subscribe();

        await WaitForConditionAsync(() => vm.CompletionItems.Count > 0, TimeSpan.FromSeconds(2));

        Assert.True(vm.IsCompletionOpen);
        Assert.Contains(vm.CompletionItems, item => item.DisplayText == "Grid");
    }

    [AvaloniaFact]
    public async Task DefinitionLookup_And_OpenLocation_Navigates_To_Target()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "XveLspTests");
        Directory.CreateDirectory(workspace);
        string filePath = Path.Combine(workspace, "Test.cs");
        string text = "class Foo {}\nclass Bar {}";
        await File.WriteAllTextAsync(filePath, text);

        LanguageLocation target = new()
        {
            FilePath = filePath,
            Range = new LanguageTextRange(
                new LanguageTextPosition(2, 1),
                new LanguageTextPosition(2, 1))
        };

        FakeLanguageService service = new("csharp")
        {
            Definitions = new[] { target }
        };

        ILanguageIntellisenseRegistry registry = new LanguageServiceRegistry(new[] { service });
        using MainWindowViewModel vm = new(languageRegistry: registry);
        using EditorServicesAdapter editor = new(vm);
        LanguageNavigationServiceAdapter navigation = new(registry, editor);

        await vm.OpenFileAsync(filePath);
        Assert.NotNull(vm.ActiveTextDocument);

        LanguagePositionContext context = new()
        {
            FilePath = filePath,
            Text = vm.ActiveTextDocument!.Document.Text,
            Offset = 0
        };

        IReadOnlyList<LanguageLocation> definitions = await navigation.FindDefinitionsAsync(context, CancellationToken.None);
        Assert.Single(definitions);

        bool opened = await editor.OpenLocationAsync(definitions[0], CancellationToken.None);
        Assert.True(opened);

        TextDocumentViewModel textDoc = vm.ActiveTextDocument!;
        int expectedOffset = textDoc.GetOffsetForLineColumn(2, 1);
        Assert.Equal(expectedOffset, textDoc.CaretOffset);
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition not met within timeout.");
    }

    private sealed class FakeLanguageService : ILanguageIntellisenseService
    {
        public FakeLanguageService(string languageId)
        {
            LanguageId = languageId;
        }

        public string LanguageId { get; }

        public IReadOnlyList<CompletionItem> Completions { get; set; } = Array.Empty<CompletionItem>();

        public IReadOnlyList<LanguageDiagnostic> Diagnostics { get; set; } = Array.Empty<LanguageDiagnostic>();

        public IReadOnlyList<LanguageLocation> Definitions { get; set; } = Array.Empty<LanguageLocation>();

        public bool CanHandle(string filePath, string? languageId)
        {
            if (!string.IsNullOrWhiteSpace(languageId)
                && string.Equals(languageId, LanguageId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return true;
        }

        public Task InitializeWorkspaceAsync(string workspacePath, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearWorkspaceAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(
            CompletionContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult(Completions);
        }

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(
            LanguageDocumentContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult(Diagnostics);
        }

        public Task<IReadOnlyList<LanguageSemanticToken>> GetSemanticTokensAsync(
            LanguageDocumentContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<LanguageSemanticToken>>(Array.Empty<LanguageSemanticToken>());
        }

        public Task<IReadOnlyList<TextEdit>> GetFormattingEditsAsync(
            LanguageDocumentContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<TextEdit>>(Array.Empty<TextEdit>());
        }

        public Task<LanguageHover?> GetHoverAsync(LanguagePositionContext context, CancellationToken ct = default)
        {
            return Task.FromResult<LanguageHover?>(null);
        }

        public Task<IReadOnlyList<LanguageLocation>> FindDefinitionsAsync(
            LanguagePositionContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult(Definitions);
        }

        public Task<IReadOnlyList<LanguageLocation>> FindReferencesAsync(
            LanguagePositionContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<LanguageLocation>>(Array.Empty<LanguageLocation>());
        }

        public Task<LanguageRenameInfo?> PrepareRenameAsync(
            LanguagePositionContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult<LanguageRenameInfo?>(null);
        }

        public Task<LanguageWorkspaceEdit?> RenameSymbolAsync(
            LanguageRenameContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult<LanguageWorkspaceEdit?>(null);
        }

        public Task<LanguageSignatureHelp?> GetSignatureHelpAsync(
            LanguagePositionContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult<LanguageSignatureHelp?>(null);
        }

        public Task<IReadOnlyList<LanguageCodeAction>> GetCodeActionsAsync(
            LanguageCodeActionContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<LanguageCodeAction>>(Array.Empty<LanguageCodeAction>());
        }

        public Task<IReadOnlyList<LanguageSymbol>> GetDocumentSymbolsAsync(
            LanguageDocumentContext context,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<LanguageSymbol>>(Array.Empty<LanguageSymbol>());
        }

        public Task<IReadOnlyList<LanguageSymbol>> GetWorkspaceSymbolsAsync(
            LanguageSymbolQuery query,
            CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<LanguageSymbol>>(Array.Empty<LanguageSymbol>());
        }
    }
}
