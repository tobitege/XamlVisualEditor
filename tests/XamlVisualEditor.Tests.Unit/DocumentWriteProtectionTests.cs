using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;
using System.Reactive.Threading.Tasks;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DocumentWriteProtectionTests : IDisposable
{
    private const string OriginalText = "class Example { }";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-WriteProtection-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact(Timeout = 30000)]
    public async Task SavingReadOnlyFile_ReportsFailureAndKeepsChanges()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string codePath = CreateReadOnlyFile("Code.cs");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        await main.OpenFileAsync(codePath).WaitAsync(cancellationToken);
        TextDocumentViewModel document = Assert.IsType<TextDocumentViewModel>(Assert.Single(main.Documents));
        document.Document.Text = "class Example { int _value; }";

        await main.SaveDocumentCommand.Execute().FirstAsync().ToTask(cancellationToken);
        await main.SaveAllCommand.Execute().FirstAsync().ToTask(cancellationToken);

        Assert.True(document.IsModified);
        Assert.Equal(string.Format(UiStrings.DocumentSaveReadOnly, "Code.cs"), main.StatusText);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(codePath).WaitAsync(cancellationToken));
        Assert.True(File.GetAttributes(codePath).HasFlag(FileAttributes.ReadOnly));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task AutoSaveOfReadOnlyFileOnShutdown_AsksAndLetsTheUserDiscard()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string codePath = CreateReadOnlyFile("Shutdown.cs");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        System.Collections.Generic.List<System.Collections.Generic.IReadOnlyList<string>> prompts = new();
        SaveChangesChoice answer = SaveChangesChoice.Cancel;
        using IDisposable handler = main.SaveChangesInteraction.RegisterHandler(interaction =>
        {
            prompts.Add(interaction.Input);
            interaction.SetOutput(answer);
        });
        await main.OpenFileAsync(codePath).WaitAsync(cancellationToken);
        TextDocumentViewModel document = Assert.IsType<TextDocumentViewModel>(Assert.Single(main.Documents));
        document.Document.Text = "class Example { int _value; }";
        main.SetAutoSaveCommand.Execute().Subscribe();

        Assert.False(await main.ConfirmShutdownAsync().WaitAsync(cancellationToken));
        Assert.Equal(["Shutdown.cs"], Assert.Single(prompts));

        answer = SaveChangesChoice.Discard;
        Assert.True(await main.ConfirmShutdownAsync().WaitAsync(cancellationToken));
        Assert.True(main.IsShutdownConfirmed);
        Assert.Equal(2, prompts.Count);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(codePath).WaitAsync(cancellationToken));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task OpenReadOnlyFile_ShowsMarkerUntilWriteProtectionIsRemoved()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string codePath = CreateReadOnlyFile("Marked.cs");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        await main.OpenFileAsync(codePath).WaitAsync(cancellationToken);
        TextDocumentViewModel document = Assert.IsType<TextDocumentViewModel>(Assert.Single(main.Documents));

        Assert.True(document.IsReadOnlyFile);
        Assert.Equal($"Marked.cs {UiStrings.ReadOnlyTitleSuffix}", DocumentTitle.Format(document.FileName, false, document.IsReadOnlyFile));
        Assert.True(document.ShowReadOnlyNotice);
        using (EditorServicesAdapter editor = new(main))
        {
            editor.SetWriteProtectionManaged(codePath, true);
            Assert.False(document.ShowReadOnlyNotice);
            Assert.True(document.IsReadOnlyFile);
            editor.SetWriteProtectionManaged(codePath, false);
            Assert.True(document.ShowReadOnlyNotice);
        }

        File.SetAttributes(codePath, FileAttributes.Normal);
        for (int attempt = 0; attempt < 100 && document.IsReadOnlyFile; attempt++)
        {
            await Task.Delay(50, cancellationToken);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.False(document.IsReadOnlyFile);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task WorkspaceEditForClosedFile_OpensDocumentInsteadOfWritingFile()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string codePath = CreateReadOnlyFile("Closed.cs");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        LanguageWorkspaceEdit edit = new()
        {
            DocumentEdits = new[]
            {
                new LanguageDocumentEdit
                {
                    FilePath = codePath,
                    Edits = new[] { new TextEdit { Offset = 6, Length = 7, NewText = "Renamed" } }
                }
            }
        };

        await main.ApplyWorkspaceEditAsync(edit);

        TextDocumentViewModel document = Assert.IsType<TextDocumentViewModel>(Assert.Single(main.Documents));
        Assert.Equal("class Renamed { }", document.Document.Text);
        Assert.True(document.IsModified);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(codePath).WaitAsync(cancellationToken));
        Assert.True(File.GetAttributes(codePath).HasFlag(FileAttributes.ReadOnly));
    }

    private string CreateReadOnlyFile(string fileName)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, OriginalText);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // Only the files this test created are made writable again, so the temporary folder can be removed.
        foreach (string path in Directory.EnumerateFiles(_root))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }
}
