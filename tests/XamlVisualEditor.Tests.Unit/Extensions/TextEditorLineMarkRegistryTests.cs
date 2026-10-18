using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

public sealed class TextEditorLineMarkRegistryTests
{
    private static readonly string FilePath = Path.Combine(Path.GetTempPath(), "marks", "a.txt");

    [Fact]
    public void The_first_layer_with_marks_wins_and_an_empty_list_clears_its_marks()
    {
        TextEditorLineMarkRegistry registry = new();
        using ITextEditorLineMarkLayer first = registry.CreateLayer("first.command");
        using ITextEditorLineMarkLayer second = registry.CreateLayer("second.command");

        second.Set(FilePath, [new TextEditorLineMark(4, 1, TextEditorLineMarkKind.Added)]);
        Assert.Equal(TextEditorLineMarkKind.Added, Assert.Single(registry.Find(FilePath, out string? command)).Kind);
        Assert.Equal("second.command", command);

        first.Set(FilePath,
        [
            new TextEditorLineMark(9, 0, TextEditorLineMarkKind.Deleted),
            new TextEditorLineMark(2, 3, TextEditorLineMarkKind.Modified)
        ]);
        Assert.Equal([2, 9], registry.Find(FilePath, out command).Select(mark => mark.StartLine));
        Assert.Equal("first.command", command);

        first.Set(FilePath, []);
        Assert.Equal(4, Assert.Single(registry.Find(FilePath, out command)).StartLine);
        Assert.Equal("second.command", command);
    }

    [Fact]
    public void Paths_are_normalized_on_set_and_find_and_compared_by_the_rules_of_the_file_system()
    {
        TextEditorLineMarkRegistry registry = new();
        using ITextEditorLineMarkLayer layer = registry.CreateLayer(null);
        List<string> changed = new();
        registry.Changed += (_, path) => changed.Add(path);

        layer.Set(Path.Combine(Path.GetTempPath(), "marks", ".", "sub", "..", "a.txt"), [new TextEditorLineMark(1, 1, TextEditorLineMarkKind.Added)]);

        Assert.Single(registry.Find(Path.Combine(Path.GetTempPath(), "marks", "sub", "..", "a.txt"), out _));
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), registry.Find(FilePath.ToUpperInvariant(), out _).Count == 1);
        Assert.Empty(registry.Find(null, out _));
        Assert.Equal([FilePath], changed);
    }

    [Fact]
    public void Dispose_removes_the_marks_reports_their_files_and_ignores_a_later_set()
    {
        TextEditorLineMarkRegistry registry = new();
        ITextEditorLineMarkLayer layer = registry.CreateLayer(null);
        string other = Path.Combine(Path.GetTempPath(), "marks", "b.txt");
        layer.Set(FilePath, [new TextEditorLineMark(1, 1, TextEditorLineMarkKind.Added)]);
        layer.Set(other, [new TextEditorLineMark(1, 1, TextEditorLineMarkKind.Added)]);
        List<string> changed = new();
        registry.Changed += (_, path) => changed.Add(path);

        layer.Dispose();
        layer.Set(FilePath, [new TextEditorLineMark(1, 1, TextEditorLineMarkKind.Modified)]);
        layer.Dispose();

        Assert.Empty(registry.Find(FilePath, out _));
        Assert.Empty(registry.Find(other, out _));
        Assert.Equal([FilePath, other], changed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Set_rejects_lines_before_the_first_and_negative_counts()
    {
        TextEditorLineMarkRegistry registry = new();
        using ITextEditorLineMarkLayer layer = registry.CreateLayer(null);

        Assert.Throws<ArgumentOutOfRangeException>(() => layer.Set(FilePath, [new TextEditorLineMark(0, 1, TextEditorLineMarkKind.Added)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.Set(FilePath, [new TextEditorLineMark(1, -1, TextEditorLineMarkKind.Added)]));
        Assert.Empty(registry.Find(FilePath, out _));
    }
}
