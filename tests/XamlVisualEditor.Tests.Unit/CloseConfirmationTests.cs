using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;
using System.Reactive.Threading.Tasks;

namespace XamlVisualEditor.Tests.Unit;

public sealed class CloseConfirmationTests : IDisposable
{
    private const string OriginalText = "class Example { }";
    private const string ChangedText = "class Example { int _value; }";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-CloseConfirmation-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact(Timeout = 30000)]
    public async Task Closing_a_modified_document_asks_and_follows_the_answer()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string codePath = CreateFile("Code.cs");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        List<IReadOnlyList<string>> prompts = new();
        SaveChangesChoice answer = SaveChangesChoice.Cancel;
        using IDisposable handler = main.SaveChangesInteraction.RegisterHandler(interaction =>
        {
            prompts.Add(interaction.Input);
            interaction.SetOutput(answer);
        });

        TextDocumentViewModel document = await OpenAndChangeAsync(main, codePath).WaitAsync(cancellationToken);
        await main.CloseDocumentCommand.Execute(document).FirstAsync().ToTask(cancellationToken);
        Assert.Equal(["Code.cs"], Assert.Single(prompts));
        Assert.Contains(document, main.Documents);

        answer = SaveChangesChoice.Discard;
        await main.CloseActiveDocumentCommand.Execute().FirstAsync().ToTask(cancellationToken);
        Assert.Empty(main.Documents);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(codePath).WaitAsync(cancellationToken));

        answer = SaveChangesChoice.Save;
        document = await OpenAndChangeAsync(main, codePath).WaitAsync(cancellationToken);
        await main.CloseDocumentCommand.Execute(document).FirstAsync().ToTask(cancellationToken);
        Assert.Empty(main.Documents);
        Assert.Equal(ChangedText, await File.ReadAllTextAsync(codePath).WaitAsync(cancellationToken));
        Assert.Equal(3, prompts.Count);

        await OpenAndChangeAsync(main, codePath, OriginalText).WaitAsync(cancellationToken);
        answer = SaveChangesChoice.Cancel;
        Assert.False(await main.ConfirmShutdownAsync());
        Assert.False(main.IsShutdownConfirmed);
        main.SetNoSaveCommand.Execute().Subscribe();
        Assert.True(await main.ConfirmShutdownAsync());
        Assert.True(main.IsShutdownConfirmed);
        Assert.Equal(4, prompts.Count);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Restoring_the_last_workspace_opens_the_most_recent_folder()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string folder = Path.Combine(_root, "Project");
        Directory.CreateDirectory(folder);
        string recentPath = Path.Combine(_root, "recent.json");
        using (MainWindowViewModel empty = new(recentFilesPath: recentPath))
        {
            Assert.False(await empty.RestoreLastWorkspaceAsync());
        }

        await File.WriteAllTextAsync(recentPath,
            "[{\"FilePath\":" + System.Text.Json.JsonSerializer.Serialize(folder) + ",\"Kind\":\"Folder\"}]").WaitAsync(cancellationToken);
        using MainWindowViewModel main = new(recentFilesPath: recentPath);
        Assert.True(await main.RestoreLastWorkspaceAsync());
        Assert.Equal(folder, main.RecentFiles.First().FilePath);

        string optionsPath = Path.Combine(_root, "editor-options.json");
        new EditorOptionsStore(optionsPath).Save(new EditorOptions { RestoreLastWorkspace = true });
        Assert.True(new EditorOptionsStore(optionsPath).Current.RestoreLastWorkspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task<TextDocumentViewModel> OpenAndChangeAsync(MainWindowViewModel main, string path,
        string text = ChangedText)
    {
        await main.OpenFileAsync(path);
        TextDocumentViewModel document = Assert.IsType<TextDocumentViewModel>(Assert.Single(main.Documents));
        document.Document.Text = text;
        Assert.True(document.IsModified);
        return document;
    }

    private string CreateFile(string name)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, OriginalText);
        return path;
    }
}
