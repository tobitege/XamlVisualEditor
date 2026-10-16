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
    private const string OutsideText = "class Outside { }";

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
    public async Task Saving_over_an_outside_change_asks_and_overwrites_or_reloads()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string codePath = CreateFile("Code.cs");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        List<string> conflicts = new();
        SaveConflictChoice answer = SaveConflictChoice.Overwrite;
        using IDisposable handler = main.SaveConflictInteraction.RegisterHandler(interaction =>
        {
            conflicts.Add(interaction.Input);
            interaction.SetOutput(answer);
        });

        TextDocumentViewModel document = await OpenAndChangeAsync(main, codePath).WaitAsync(cancellationToken);
        await File.WriteAllTextAsync(codePath, OutsideText, cancellationToken);
        await main.SaveDocumentCommand.Execute().FirstAsync().ToTask(cancellationToken);

        Assert.Equal(["Code.cs"], conflicts);
        Assert.Equal(ChangedText, await File.ReadAllTextAsync(codePath, cancellationToken));
        Assert.False(document.IsModified);

        document.Document.Text = OriginalText;
        await File.WriteAllTextAsync(codePath, OutsideText, cancellationToken);
        answer = SaveConflictChoice.Reload;
        await main.SaveDocumentCommand.Execute().FirstAsync().ToTask(cancellationToken);

        Assert.Equal(2, conflicts.Count);
        Assert.Equal(OutsideText, document.Document.Text);
        Assert.False(document.IsModified);
        Assert.Equal(OutsideText, await File.ReadAllTextAsync(codePath, cancellationToken));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Reloading_a_designer_document_takes_the_disk_text_into_the_editor_and_the_designer()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string xaml = "<UserControl xmlns=\"https://github.com/avaloniaui\"><Button /></UserControl>";
        const string outsideXaml = "<UserControl xmlns=\"https://github.com/avaloniaui\"><TextBlock /></UserControl>";
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "View.axaml");
        await File.WriteAllTextAsync(path, xaml, cancellationToken);
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        using IDisposable handler = main.SaveConflictInteraction.RegisterHandler(interaction =>
            interaction.SetOutput(SaveConflictChoice.Reload));
        await main.OpenFileAsync(path).WaitAsync(cancellationToken);
        DesignerDocumentViewModel document = Assert.IsType<DesignerDocumentViewModel>(Assert.Single(main.Documents));
        document.CodeEditor.Document.Text = xaml.Replace("Button", "CheckBox", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, outsideXaml, cancellationToken);

        await main.SaveDocumentCommand.Execute().FirstAsync().ToTask(cancellationToken);

        Assert.Equal(outsideXaml, document.CodeEditor.Document.Text);
        Assert.Equal(outsideXaml, document.SyncEngine.CurrentText);
        Assert.False(document.IsModified);
        Assert.Equal(outsideXaml, await File.ReadAllTextAsync(path, cancellationToken));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task A_declined_conflict_stops_automatic_saves_from_asking_again_until_a_manual_save()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string codePath = CreateFile("Code.cs");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        int conflicts = 0;
        using IDisposable handler = main.SaveConflictInteraction.RegisterHandler(interaction =>
        {
            conflicts++;
            interaction.SetOutput(SaveConflictChoice.Cancel);
        });

        TextDocumentViewModel document = await OpenAndChangeAsync(main, codePath).WaitAsync(cancellationToken);
        await File.WriteAllTextAsync(codePath, OutsideText, cancellationToken);
        main.SetAutoSaveCommand.Execute().Subscribe();
        await main.AutoSaveAsync(document).WaitAsync(cancellationToken);
        await main.AutoSaveAsync(document).WaitAsync(cancellationToken);

        Assert.Equal(1, conflicts);

        await main.SaveDocumentCommand.Execute().FirstAsync().ToTask(cancellationToken);

        Assert.Equal(2, conflicts);
        Assert.Equal(OutsideText, await File.ReadAllTextAsync(codePath, cancellationToken));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task A_declined_conflict_of_an_automatic_save_on_shutdown_asks_and_lets_the_user_discard()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string codePath = CreateFile("Code.cs");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        List<IReadOnlyList<string>> prompts = new();
        using IDisposable saveHandler = main.SaveChangesInteraction.RegisterHandler(interaction =>
        {
            prompts.Add(interaction.Input);
            interaction.SetOutput(SaveChangesChoice.Discard);
        });
        using IDisposable conflictHandler = main.SaveConflictInteraction.RegisterHandler(interaction =>
            interaction.SetOutput(SaveConflictChoice.Cancel));

        await OpenAndChangeAsync(main, codePath).WaitAsync(cancellationToken);
        await File.WriteAllTextAsync(codePath, OutsideText, cancellationToken);
        main.SetAutoSaveCommand.Execute().Subscribe();

        Assert.True(await main.ConfirmShutdownAsync().WaitAsync(cancellationToken));
        Assert.True(main.IsShutdownConfirmed);
        Assert.Equal(["Code.cs"], Assert.Single(prompts));
        Assert.Equal(OutsideText, await File.ReadAllTextAsync(codePath, cancellationToken));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task An_automatic_save_on_shutdown_saves_the_other_documents_when_one_cannot_be_saved()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string conflictedPath = CreateFile("Conflicted.cs");
        string writablePath = CreateFile("Writable.cs");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        List<IReadOnlyList<string>> prompts = new();
        using IDisposable saveHandler = main.SaveChangesInteraction.RegisterHandler(interaction =>
        {
            prompts.Add(interaction.Input);
            interaction.SetOutput(SaveChangesChoice.Discard);
        });
        using IDisposable conflictHandler = main.SaveConflictInteraction.RegisterHandler(interaction =>
            interaction.SetOutput(SaveConflictChoice.Cancel));

        await main.OpenFileAsync(conflictedPath).WaitAsync(cancellationToken);
        await main.OpenFileAsync(writablePath).WaitAsync(cancellationToken);
        foreach (TextDocumentViewModel document in main.Documents.OfType<TextDocumentViewModel>())
            document.Document.Text = ChangedText;
        // The document that cannot be saved comes first, so the other one is only saved when every document is tried.
        Assert.Equal(["Conflicted.cs", "Writable.cs"], main.Documents.Where(document => document.IsModified).Select(document => document.FileName));
        await File.WriteAllTextAsync(conflictedPath, OutsideText, cancellationToken);
        main.SetAutoSaveCommand.Execute().Subscribe();

        Assert.True(await main.ConfirmShutdownAsync().WaitAsync(cancellationToken));
        Assert.Equal(["Conflicted.cs"], Assert.Single(prompts));
        Assert.Equal(ChangedText, await File.ReadAllTextAsync(writablePath, cancellationToken));
        Assert.Equal(OutsideText, await File.ReadAllTextAsync(conflictedPath, cancellationToken));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Reloading_a_conflicted_document_on_shutdown_closes_without_asking()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string codePath = CreateFile("Code.cs");
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        int prompts = 0;
        using IDisposable saveHandler = main.SaveChangesInteraction.RegisterHandler(interaction =>
        {
            prompts++;
            interaction.SetOutput(SaveChangesChoice.Cancel);
        });
        using IDisposable conflictHandler = main.SaveConflictInteraction.RegisterHandler(interaction =>
            interaction.SetOutput(SaveConflictChoice.Reload));

        TextDocumentViewModel document = await OpenAndChangeAsync(main, codePath).WaitAsync(cancellationToken);
        await File.WriteAllTextAsync(codePath, OutsideText, cancellationToken);
        main.SetAutoSaveCommand.Execute().Subscribe();

        Assert.True(await main.ConfirmShutdownAsync().WaitAsync(cancellationToken));
        Assert.Equal(0, prompts);
        Assert.False(document.IsModified);
        Assert.Equal(OutsideText, document.Document.Text);
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
