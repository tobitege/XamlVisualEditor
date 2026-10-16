using System;
using System.IO;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DocumentPathDisplayTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xve-path-display-" + Guid.NewGuid().ToString("N"));

    public DocumentPathDisplayTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void FormatWindow_ShowsFileNameByDefaultAndFullPathOnRequest()
    {
        string path = Path.Combine(_directory, "Form.axaml");

        Assert.Equal(UiStrings.ApplicationName + " — Form.axaml", DocumentTitle.FormatWindow("Form.axaml", path, showFullPath: false));
        Assert.Equal(UiStrings.ApplicationName + " — " + path, DocumentTitle.FormatWindow("Form.axaml", path, showFullPath: true));
        Assert.Equal(UiStrings.ApplicationName, DocumentTitle.FormatWindow(null, null, showFullPath: true));
    }

    [Fact]
    public void ShowFullPathInTitle_IsOffByDefaultAndPersists()
    {
        string file = Path.Combine(_directory, "options.json");
        Assert.False(new EditorOptions().ShowFullPathInTitle);

        new EditorOptionsStore(file).Save(new EditorOptions { ShowFullPathInTitle = true });

        Assert.True(new EditorOptionsStore(file).Current.ShowFullPathInTitle);
    }

    [AvaloniaFact]
    public async Task OptionsDialog_SavesShowFullPathInTitle()
    {
        EditorOptionsStore store = new(Path.Combine(_directory, "dialog-options.json"));
        using OptionsDialogViewModel dialog = new(store);
        Assert.False(dialog.ShowFullPathInTitle);

        dialog.ShowFullPathInTitle = true;
        await dialog.SaveCommand.Execute().ToTask();

        Assert.Null(dialog.Error);
        Assert.True(store.Current.ShowFullPathInTitle);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowTitle_FollowsTheOption(bool showFullPath)
    {
        string path = Path.Combine(_directory, "Sample.xml");
        EditorOptionsStore store = new();
        store.Save(new EditorOptions { ShowFullPathInTitle = showFullPath });
        using MainWindowViewModel viewModel = new(editorOptions: store);
        using TextDocumentViewModel document = new(path);
        viewModel.Documents.Add(document);

        viewModel.ActiveDocument = document;

        Assert.Equal(UiStrings.ApplicationName + " — " + (showFullPath ? path : "Sample.xml"), viewModel.Title);
    }

    [AvaloniaFact]
    public void DocumentTabToolTip_ShowsTheFullPathOfFileDocuments()
    {
        string path = Path.Combine(_directory, "Tab.xml");
        using TextDocumentViewModel document = new(path);

        Assert.Equal(path, DockableToolTipConverter.GetToolTip(new TextDocument(document), "Tab.xml"));
        Assert.Equal("Canvas", DockableToolTipConverter.GetToolTip(new InfiniteCanvasDocument(), "Canvas"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
