using Avalonia.Headless.XUnit;
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
