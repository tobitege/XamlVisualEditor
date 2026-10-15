using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.CodeEditor;
using XamlVisualEditor.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Xaml.Ast;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class SelectedElementMarkerTests : IDisposable
{
    private const string Xaml = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <StackPanel>
                <TextBlock Text="First" />
                <TextBlock
                    x:Name="Second"
                    Text="Second" />
            </StackPanel>
        </UserControl>
        """;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-SelectedElement-" + Guid.NewGuid().ToString("N"));

    public SelectedElementMarkerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("<A />", 5)]
    [InlineData("<A\n  B=\"x\"\n  C=\"y\" />", 21)]
    [InlineData("<A B=\"a/>b\" />", 14)]
    [InlineData("<A><B /><B></B></A>", 19)]
    [InlineData("<A><!-- </A> --><![CDATA[</A>]]><?x </A>?></A>", 46)]
    [InlineData("<A><A></A></A><A />", 14)]
    public void FindEnd_Returns_The_Offset_After_The_Element(string text, int expected)
    {
        Assert.Equal(expected, XmlElementExtent.FindEnd(text, 0));
    }

    [Theory]
    [InlineData("<A>")]
    [InlineData("<A B=\"unterminated />")]
    [InlineData("A />")]
    [InlineData("")]
    public void FindEnd_Returns_Minus_One_When_The_Element_Does_Not_End(string text)
    {
        Assert.Equal(-1, XmlElementExtent.FindEnd(text, 0));
    }

    [Theory]
    [InlineData("<A />", 5)]
    [InlineData("<A B=\"a>b\"\n  C='x' >text</A>", 20)]
    [InlineData("<A><B /></A>", 3)]
    [InlineData("<A B=\"unterminated >", -1)]
    [InlineData("A />", -1)]
    public void FindStartTagEnd_Returns_The_Offset_After_The_Start_Tag(string text, int expected)
    {
        Assert.Equal(expected, XmlElementExtent.FindStartTagEnd(text, 0));
    }

    [Fact]
    public void SourceLineRange_Contains_Its_Inclusive_Lines()
    {
        SourceLineRange range = new(5, 7);

        Assert.True(range.Contains(5));
        Assert.True(range.Contains(7));
        Assert.False(range.Contains(8));
    }

    [AvaloniaFact]
    public async Task Designer_Selection_Marks_The_Element_Lines_And_Reveals_Its_Start()
    {
        using DesignerDocumentViewModel document = await LoadAsync();
        List<int> revealedLines = new();
        using IDisposable reveal = document.CodeEditor.RevealLineRequests.Subscribe(revealedLines.Add);
        MutableAstObjectNode second = FindTextBlocks(document)[1];

        document.SetSelectedNode(second.Id, SyncSource.DesignSurface);

        Assert.Equal(new SourceLineRange(5, 7), document.CodeEditor.SelectedElementLines);
        Assert.Equal([5], revealedLines);
    }

    [AvaloniaFact]
    public async Task Code_Editor_Selection_Marks_Without_Scrolling_And_Clearing_Removes_The_Marker()
    {
        using DesignerDocumentViewModel document = await LoadAsync();
        List<int> revealedLines = new();
        using IDisposable reveal = document.CodeEditor.RevealLineRequests.Subscribe(revealedLines.Add);
        MutableAstObjectNode second = FindTextBlocks(document)[1];

        document.SetSelectedNode(second.Id, SyncSource.CodeEditor);
        Assert.Equal(new SourceLineRange(5, 7), document.CodeEditor.SelectedElementLines);
        Assert.Empty(revealedLines);

        document.SetSelectedNode(null, SyncSource.DesignSurface);
        Assert.Null(document.CodeEditor.SelectedElementLines);
    }

    [AvaloniaFact]
    public async Task Designer_Selection_Leaves_The_Code_Alone_When_Following_Is_Off()
    {
        using DesignerDocumentViewModel document = await LoadAsync();
        List<int> revealedLines = new();
        using IDisposable reveal = document.CodeEditor.RevealLineRequests.Subscribe(revealedLines.Add);
        int caretBefore = document.CodeEditor.CaretOffset;
        MutableAstObjectNode second = FindTextBlocks(document)[1];

        document.CodeEditor.FollowDesignerSelection = false;
        document.SetSelectedNode(second.Id, SyncSource.DesignSurface);

        Assert.Null(document.CodeEditor.SelectedElementLines);
        Assert.Empty(revealedLines);
        Assert.Equal(caretBefore, document.CodeEditor.CaretOffset);

        document.CodeEditor.FollowDesignerSelection = true;
        Assert.Equal(new SourceLineRange(5, 7), document.CodeEditor.SelectedElementLines);
    }

    [AvaloniaTheory]
    [InlineData(5, 10, 5, 7)]
    [InlineData(6, 13, 5, 7)]
    [InlineData(3, 6, 3, 3)]
    [InlineData(8, 7, 8, 8)]
    public async Task Diagnostic_Marks_The_Start_Tag_Around_Its_Position_Or_Its_Line(
        int line, int column, int expectedStart, int expectedEnd)
    {
        using DesignerDocumentViewModel document = await LoadAsync();
        List<int> revealedLines = new();
        using IDisposable reveal = document.CodeEditor.RevealLineRequests.Subscribe(revealedLines.Add);

        document.CodeEditor.MarkDiagnostic(line, column);

        Assert.Equal(new SourceLineRange(expectedStart, expectedEnd), document.CodeEditor.DiagnosticLines);
        Assert.Equal([expectedStart], revealedLines);
        Assert.Equal(document.CodeEditor.GetOffsetForLineColumn(line, column), document.CodeEditor.CaretOffset);
        Assert.Equal(0, document.CodeEditor.SelectionLength);
    }

    [AvaloniaFact]
    public async Task Editing_The_Text_Removes_The_Diagnostic_Marker()
    {
        using DesignerDocumentViewModel document = await LoadAsync();
        document.CodeEditor.MarkDiagnostic(5, 10);
        Assert.NotNull(document.CodeEditor.DiagnosticLines);

        document.CodeEditor.Document.Insert(0, " ");

        Assert.Null(document.CodeEditor.DiagnosticLines);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Editor_Services_Mark_A_Diagnostic_Location_But_Only_Select_A_Plain_Location()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(_root, "View.axaml");
        File.WriteAllText(path, Xaml);
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        using EditorServicesAdapter editor = new(main);
        LanguageLocation location = new()
        {
            FilePath = path,
            Range = new LanguageTextRange(new LanguageTextPosition(6, 13), new LanguageTextPosition(6, 19))
        };

        Assert.True(await editor.OpenLocationAsync(location, cancellationToken));
        DesignerDocumentViewModel document = Assert.IsType<DesignerDocumentViewModel>(Assert.Single(main.Documents));
        Assert.Null(document.CodeEditor.DiagnosticLines);
        Assert.Equal(6, document.CodeEditor.SelectionLength);

        Assert.True(await editor.OpenDiagnosticLocationAsync(location, cancellationToken));
        Assert.Equal(new SourceLineRange(5, 7), document.CodeEditor.DiagnosticLines);
        Assert.Equal(0, document.CodeEditor.SelectionLength);
        Assert.Equal(document.CodeEditor.GetOffsetForLineColumn(6, 13), document.CodeEditor.CaretOffset);
    }

    [AvaloniaFact]
    public async Task Text_From_Another_Source_Replaces_Only_The_Changed_Span()
    {
        using DesignerDocumentViewModel document = await LoadAsync();
        string before = document.CodeEditor.Document.Text;
        string after = before.Replace("Text=\"First\"", "Text=\"Changed\"", StringComparison.Ordinal);
        List<(int Offset, int RemovalLength, string Inserted)> changes = new();
        document.CodeEditor.Document.Changed += (_, change) =>
            changes.Add((change.Offset, change.RemovalLength, change.InsertedText.Text));

        document.SyncEngine.NotifyTextChanged(after, SyncSource.DesignSurface);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(after, document.CodeEditor.Document.Text);
        Assert.Equal((before.IndexOf("First\"", StringComparison.Ordinal), "First".Length, "Changed"), Assert.Single(changes));
        Assert.True(document.CodeEditor.IsModified);
        Assert.False(document.CodeEditor.HasPendingTextChanges);
    }

    [AvaloniaFact]
    public async Task Pending_Typing_And_A_Designer_Change_Both_Reach_The_Document()
    {
        using DesignerDocumentViewModel document = await LoadAsync();
        string before = document.CodeEditor.Document.Text;
        const string typed = "<!-- typed -->";
        document.CodeEditor.Document.Insert(0, typed);
        Assert.True(document.CodeEditor.HasPendingTextChanges);
        string designerText = before.Replace("Text=\"Second\"", "Text=\"Designer\"", StringComparison.Ordinal);

        document.SyncEngine.NotifyTextChanged(designerText, SyncSource.DesignSurface);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        document.CodeEditor.FlushPendingTextChanges();

        Assert.Equal(typed + designerText, document.CodeEditor.Document.Text);
        Assert.Equal(typed + designerText, document.SyncEngine.CurrentText);
    }

    [AvaloniaFact]
    public async Task Designer_Document_Reports_Caret_Position_Changes()
    {
        using DesignerDocumentViewModel document = await LoadAsync();
        List<string?> changed = new();
        document.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        document.CodeEditor.CurrentLine = 5;
        document.CodeEditor.CurrentColumn = 9;

        Assert.Equal(5, document.CurrentLine);
        Assert.Equal(9, document.CurrentColumn);
        Assert.Contains(nameof(DesignerDocumentViewModel.CurrentLine), changed);
        Assert.Contains(nameof(DesignerDocumentViewModel.CurrentColumn), changed);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<DesignerDocumentViewModel> LoadAsync()
    {
        string path = Path.Combine(_root, "View.axaml");
        File.WriteAllText(path, Xaml);
        DesignerDocumentViewModel document = new(path);
        await document.LoadAsync();
        return document;
    }

    private static List<MutableAstObjectNode> FindTextBlocks(DesignerDocumentViewModel document)
    {
        List<MutableAstObjectNode> textBlocks = new();
        MutableAstObjectNode? root = document.SyncEngine.CurrentDocument?.Root;
        Assert.NotNull(root);
        Collect(root);
        return textBlocks;

        void Collect(MutableAstObjectNode node)
        {
            if (node.TypeName == "TextBlock")
                textBlocks.Add(node);
            foreach (MutableAstObjectNode child in node.Children.OfType<MutableAstObjectNode>())
                Collect(child);
        }
    }
}
