using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Xaml.Ast;
using Xunit;
using static XamlVisualEditor.Tests.UI.ExtensionCommandMenuIntegrationTests;

namespace XamlVisualEditor.Tests.UI;

public sealed class WorkspaceEditTests : IDisposable
{
    private const string XamlText = "<UserControl xmlns=\"https://github.com/avaloniaui\">\n  <TextBlock Text=\"Old\" />\n</UserControl>\n";
    private const string CodeText = "class Old { }\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"xve-workspace-edit-{Guid.NewGuid():N}");

    [AvaloniaFact]
    public async Task Code_Action_Edits_An_Open_Designer_Document_In_Code_And_Ast_As_One_Undo_Step()
    {
        string xamlPath = CreateFile("View.axaml", XamlText);
        string codePath = CreateFile("Code.cs", CodeText);
        RenameService service = new(new LanguageWorkspaceEdit
        {
            DocumentEdits = new[] { RenameOld(xamlPath, XamlText) }
        });
        using ExtensionMenuTestHost host = new(languageServices: new[] { service });
        await host.ActivateAsync();
        await host.OpenFileAsync(xamlPath);
        DesignerDocumentViewModel designer = Assert.IsType<DesignerDocumentViewModel>(host.ViewModel.ActiveDocument);
        await host.OpenFileAsync(codePath);

        await host.ExecuteCommandAsync("navigation.codeActions");
        await WaitForAsync(() => host.ViewModel.StatusText == string.Format(UiStrings.ActionApplied, "Rename"));

        string renamed = XamlText.Replace("Old", "New", StringComparison.Ordinal);
        Assert.Equal(string.Format(UiStrings.ActionApplied, "Rename"), host.ViewModel.StatusText);
        Assert.Equal(renamed, designer.CodeEditor.Document.Text);
        Assert.Equal(renamed, designer.SyncEngine.CurrentText);
        Assert.Equal("New", TextBlockText(designer));

        await host.OpenFileAsync(xamlPath);
        await host.ExecuteCommandAsync("editing.undo");

        Assert.Equal(XamlText, designer.CodeEditor.Document.Text);
        Assert.Equal("Old", TextBlockText(designer));
    }

    [AvaloniaFact]
    public async Task Rename_Opens_Closed_Files_Without_Activating_Them_And_Reports_Only_Applied_Edits()
    {
        string openXamlPath = CreateFile("Open.axaml", XamlText);
        string closedXamlPath = CreateFile("Closed.axaml", XamlText);
        string codePath = CreateFile("Code.cs", CodeText);
        RenameService service = new(new LanguageWorkspaceEdit
        {
            DocumentEdits = new[]
            {
                RenameOld(openXamlPath, XamlText),
                RenameOld(closedXamlPath, XamlText),
                RenameOld(codePath, CodeText)
            }
        });
        using ExtensionMenuTestHost host = new(languageServices: new[] { service });
        using IDisposable answer = host.ViewModel.RenameSymbolInteraction.RegisterHandler(context => context.SetOutput("New"));
        await host.ActivateAsync();
        await host.OpenFileAsync(openXamlPath);
        await host.OpenFileAsync(codePath);
        TextDocumentViewModel code = Assert.IsType<TextDocumentViewModel>(host.ViewModel.ActiveDocument);
        int openDocumentChanges = 0;
        host.Editor.OpenDocumentsChanged += (_, _) => openDocumentChanges++;

        await host.ExecuteCommandAsync("navigation.renameSymbol");
        await WaitForAsync(() => host.ViewModel.Documents.Count == 3);

        Assert.Equal(1, openDocumentChanges);
        Assert.Same(code, host.ViewModel.ActiveDocument);
        Assert.Equal("class New { }\n", code.Document.Text);
        foreach (string path in new[] { openXamlPath, closedXamlPath })
        {
            DesignerDocumentViewModel designer = Assert.IsType<DesignerDocumentViewModel>(
                host.ViewModel.Documents.Single(document => document.FilePath == path));
            Assert.Equal("New", TextBlockText(designer));
        }
        Assert.Equal(string.Format(UiStrings.SymbolRenamed, "Old", "New"), host.ViewModel.StatusText);

        await host.ExecuteCommandAsync("editing.undo");

        Assert.Equal(CodeText, code.Document.Text);
        DesignerDocumentViewModel first = Assert.IsType<DesignerDocumentViewModel>(
            host.ViewModel.Documents.Single(document => document.FilePath == openXamlPath));
        Assert.Equal("Old", TextBlockText(first));

        service.Edit = new LanguageWorkspaceEdit
        {
            DocumentEdits = new[] { RenameOld(Path.Combine(_root, "Missing.axaml"), XamlText) }
        };
        await host.ExecuteCommandAsync("navigation.renameSymbol");
        await host.FlushAsync();

        Assert.Equal(UiStrings.RenameNoChanges, host.ViewModel.StatusText);

        Assert.True(await host.Editor.CloseDocumentAsync(closedXamlPath, CancellationToken.None));
        Assert.Equal(2, openDocumentChanges);
    }

    [AvaloniaFact]
    public async Task A_Refused_Edit_Changes_None_Of_Its_Documents()
    {
        string xamlPath = CreateFile("View.axaml", XamlText);
        string codePath = CreateFile("Code.cs", CodeText);
        RenameService service = new(new LanguageWorkspaceEdit
        {
            DocumentEdits = new[]
            {
                RenameOld(xamlPath, XamlText),
                new LanguageDocumentEdit
                {
                    FilePath = codePath,
                    ExpectedText = "class Other { }\n",
                    Edits = RenameOld(codePath, CodeText).Edits
                }
            }
        });
        using ExtensionMenuTestHost host = new(languageServices: new[] { service });
        using IDisposable answer = host.ViewModel.RenameSymbolInteraction.RegisterHandler(context => context.SetOutput("New"));
        await host.ActivateAsync();
        await host.OpenFileAsync(xamlPath);
        DesignerDocumentViewModel designer = Assert.IsType<DesignerDocumentViewModel>(host.ViewModel.ActiveDocument);
        await host.OpenFileAsync(codePath);
        TextDocumentViewModel code = Assert.IsType<TextDocumentViewModel>(host.ViewModel.ActiveDocument);

        Exception? failure = await Record.ExceptionAsync(() => host.ExecuteCommandAsync("navigation.renameSymbol"));
        await WaitForAsync(() => host.ViewModel.StatusText.Contains(codePath, StringComparison.Ordinal));

        Assert.Equal(string.Format(UiStrings.CodeBehindRenameDocumentChanged, codePath), failure?.Message);
        Assert.Contains(string.Format(UiStrings.CodeBehindRenameDocumentChanged, codePath), host.ViewModel.StatusText);
        Assert.Equal(XamlText, designer.CodeEditor.Document.Text);
        Assert.Equal("Old", TextBlockText(designer));
        Assert.Equal(CodeText, code.Document.Text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string CreateFile(string name, string text)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static LanguageDocumentEdit RenameOld(string filePath, string text) => new()
    {
        FilePath = filePath,
        Edits = new[] { new TextEdit { Offset = text.IndexOf("Old", StringComparison.Ordinal), Length = 3, NewText = "New" } }
    };

    private static string? TextBlockText(DesignerDocumentViewModel designer)
    {
        MutableAstObjectNode root = Assert.IsType<MutableAstObjectNode>(designer.SyncEngine.CurrentDocument?.Root);
        MutableAstObjectNode textBlock = Assert.IsType<MutableAstObjectNode>(Assert.Single(root.Children));
        return textBlock.GetPropertyValue("Text");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class RenameService(LanguageWorkspaceEdit edit) : ILanguageIntellisenseService
    {
        public LanguageWorkspaceEdit Edit { get; set; } = edit;

        public string LanguageId => "csharp";

        public bool CanHandle(string filePath, string? languageId) =>
            filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        public Task InitializeWorkspaceAsync(string workspacePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task ClearWorkspaceAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(CompletionContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(LanguageDocumentContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());

        public Task<IReadOnlyList<LanguageSemanticToken>> GetSemanticTokensAsync(LanguageDocumentContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageSemanticToken>>(Array.Empty<LanguageSemanticToken>());

        public Task<IReadOnlyList<TextEdit>> GetFormattingEditsAsync(LanguageDocumentContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TextEdit>>(Array.Empty<TextEdit>());

        public Task<LanguageHover?> GetHoverAsync(LanguagePositionContext context, CancellationToken ct = default)
            => Task.FromResult<LanguageHover?>(null);

        public Task<IReadOnlyList<LanguageLocation>> FindDefinitionsAsync(LanguagePositionContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageLocation>>(Array.Empty<LanguageLocation>());

        public Task<IReadOnlyList<LanguageLocation>> FindReferencesAsync(LanguagePositionContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageLocation>>(Array.Empty<LanguageLocation>());

        public Task<LanguageRenameInfo?> PrepareRenameAsync(LanguagePositionContext context, CancellationToken ct = default)
            => Task.FromResult<LanguageRenameInfo?>(new LanguageRenameInfo
            {
                Name = "Old",
                Range = new LanguageTextRange(new LanguageTextPosition(1, 7), new LanguageTextPosition(1, 10))
            });

        public Task<LanguageWorkspaceEdit?> RenameSymbolAsync(LanguageRenameContext context, CancellationToken ct = default)
            => Task.FromResult<LanguageWorkspaceEdit?>(Edit);

        public Task<LanguageSignatureHelp?> GetSignatureHelpAsync(LanguagePositionContext context, CancellationToken ct = default)
            => Task.FromResult<LanguageSignatureHelp?>(null);

        public Task<IReadOnlyList<LanguageCodeAction>> GetCodeActionsAsync(LanguageCodeActionContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageCodeAction>>(new[] { new LanguageCodeAction { Title = "Rename", Edit = Edit } });

        public Task<IReadOnlyList<LanguageSymbol>> GetDocumentSymbolsAsync(LanguageDocumentContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageSymbol>>(Array.Empty<LanguageSymbol>());

        public Task<IReadOnlyList<LanguageSymbol>> GetWorkspaceSymbolsAsync(LanguageSymbolQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageSymbol>>(Array.Empty<LanguageSymbol>());
    }
}
