using System;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DocumentOpenFailureTests : IDisposable
{
    private const string Xaml = "<UserControl xmlns=\"https://github.com/avaloniaui\" />";

    // "Größe" in Windows-1252: the umlaut bytes are invalid UTF-8.
    private static readonly byte[] s_windows1252Text = { 0x47, 0x72, 0xF6, 0xDF, 0x65 };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-OpenFailure-" + Guid.NewGuid().ToString("N"));

    public DocumentOpenFailureTests()
    {
        Directory.CreateDirectory(_root);
    }

    [AvaloniaTheory(Timeout = 30000)]
    [InlineData("Legacy.cs")]
    [InlineData("Legacy.axaml")]
    public async Task Opening_a_file_that_is_not_utf8_reports_it_and_leaves_no_tab(string fileName)
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string invalidPath = Path.Combine(_root, fileName);
        await File.WriteAllBytesAsync(invalidPath, s_windows1252Text, cancellationToken);
        string validPath = Path.Combine(_root, "Valid.cs");
        await File.WriteAllTextAsync(validPath, "class Valid { }", cancellationToken);
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));

        await main.OpenPathCommand.Execute(invalidPath).FirstAsync().ToTask(cancellationToken);

        Assert.Empty(main.Documents);
        Assert.Null(main.ActiveDocument);
        Assert.Equal(string.Format(UiStrings.DocumentOpenFailedNamed, fileName, UiStrings.DocumentNotUtf8Reason), main.StatusText);

        await main.OpenPathCommand.Execute(validPath).FirstAsync().ToTask(cancellationToken);
        IEditorDocumentViewModel document = Assert.Single(main.Documents);
        Assert.Equal(validPath, document.FilePath);
        Assert.Same(document, main.ActiveDocument);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Opening_an_open_file_through_another_spelling_focuses_the_open_document()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string folder = Path.Combine(_root, "Views");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "Foo.axaml");
        await File.WriteAllTextAsync(path, Xaml, cancellationToken);
        string otherPath = Path.Combine(_root, "Other.cs");
        await File.WriteAllTextAsync(otherPath, "class Other { }", cancellationToken);
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));

        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "Only Windows and macOS file names ignore case.");
        await main.OpenFileAsync(path).WaitAsync(cancellationToken);
        IEditorDocumentViewModel document = Assert.Single(main.Documents);
        await main.OpenFileAsync(otherPath).WaitAsync(cancellationToken);

        await main.OpenFileAsync(path.ToUpperInvariant()).WaitAsync(cancellationToken);
        Assert.Equal(2, main.Documents.Count);
        Assert.Same(document, main.ActiveDocument);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
