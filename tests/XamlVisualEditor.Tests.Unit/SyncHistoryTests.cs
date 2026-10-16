using System.Collections.Immutable;
using System.Globalization;
using XamlVisualEditor.Core;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class SyncHistoryTests
{
    private const string Document = "<Canvas xmlns=\"https://github.com/avaloniaui\" Width=\"100\" />";

    [Fact]
    public void UndoHistoryKeepsOnlyTheNewestFramesUpToItsDepth()
    {
        Guid nodeId = Guid.NewGuid();
        using UndoRedoService history = new(maxDepth: 3);
        for (int value = 1; value <= 5; value++)
        {
            history.RecordChange(new PropertyValueChanged(nodeId, "Width", null, Format(value)));
            history.CommitBatch("Set width " + Format(value));
        }

        Assert.Equal(3, history.UndoCount);
        for (int expected = 5; expected >= 3; expected--)
        {
            PropertyValueChanged inverse = Assert.IsType<PropertyValueChanged>(Assert.Single(history.Undo()!));
            Assert.Equal(Format(expected), inverse.OldValue);
        }

        Assert.False(history.CanUndo);
        Assert.Null(history.Undo());
        Assert.Equal(3, history.RedoCount);
        while (history.CanRedo) history.Redo();
        Assert.Equal(3, history.UndoCount);
    }

    [Fact]
    public void UndoHistoryHasABoundedDefaultDepthAndRejectsAnEmptyOne()
    {
        using UndoRedoService history = new();

        Assert.Equal(UndoRedoService.DefaultMaxDepth, history.MaxDepth);
        Assert.Throws<ArgumentOutOfRangeException>(() => new UndoRedoService(0));
    }

    [Fact]
    public async Task EnlistRejectsASecondChangeForTheSameDocumentAndChangesAfterCompletion()
    {
        using SyncEngine engine = await LoadAsync();
        string code = "before";

        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Rename"))
        {
            edit.Enlist(new DocumentCompanionChange("View.cs", "before", "after", () => code, value => code = value));
            Assert.Throws<InvalidOperationException>(() =>
                edit.Enlist(new DocumentCompanionChange("View.cs", "before", "other", () => code, value => code = value)));
            edit.Rollback();
            Assert.Throws<InvalidOperationException>(() =>
                edit.Enlist(new DocumentCompanionChange("Other.cs", "a", "b", () => "a", _ => { })));
        }

        Assert.Equal("before", code);
        Assert.Equal(0, engine.UndoRedo.UndoCount);
    }

    [Fact]
    public async Task CompanionUndoAndRedoRequireTheirFrameOnTopOfTheHistory()
    {
        using SyncEngine engine = await LoadAsync();
        const string before = "class View { int before; }";
        const string after = "class View { int after; }";
        string code = before;
        DocumentCompanionChange rename = new("View.cs", before, after, () => code, value => code = value);

        SetWidth(engine, "200", rename);
        Assert.Equal(after, code);
        Assert.True(engine.UndoRedo.CanUndoCompanion(rename));

        SetWidth(engine, "300");
        Assert.False(engine.UndoRedo.CanUndoCompanion(rename));
        Assert.Throws<InvalidOperationException>(() => engine.Undo(rename));
        Assert.Equal("300", Width(engine));
        Assert.Equal(after, code);

        engine.Undo();
        Assert.True(engine.UndoRedo.CanUndoCompanion(rename));
        Assert.False(engine.UndoRedo.CanRedoCompanion(rename));
        engine.Undo(rename);
        Assert.Equal(before, code);
        Assert.Equal("100", Width(engine));

        Assert.True(engine.UndoRedo.CanRedoCompanion(rename));
        engine.Redo(rename);
        Assert.Equal(after, code);
        Assert.Equal("200", Width(engine));
    }

    [Fact]
    public void FailedSnapshotRestoreRollsTheCompanionBackAndKeepsTheUndoEntry()
    {
        using UndoRedoService history = new();
        string text = "after";
        DocumentCompanionChange change = new("Other.cs", "before", "after", () => text, value => text = value);
        DocumentSnapshot before = Snapshot("<Canvas />");
        DocumentSnapshot after = Snapshot("<Canvas Width=\"1\" />");
        List<DocumentSnapshot> restored = new();
        Assert.True(history.CommitSnapshot("Linked edit", before, after, [change]));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => history.Undo(snapshot =>
        {
            if (ReferenceEquals(snapshot, before)) throw new InvalidOperationException("Restore failed.");
            restored.Add(snapshot);
        }));

        Assert.Equal("Restore failed.", failure.Message);
        Assert.Equal("after", text);
        Assert.Same(after, Assert.Single(restored));
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.True(history.CanUndoCompanion(change));
    }

    private static async Task<SyncEngine> LoadAsync()
    {
        SyncEngine engine = new(new XamlParsingService(), new XamlSerializationService());
        await engine.LoadAsync(Document);
        return engine;
    }

    private static void SetWidth(SyncEngine engine, string width, DocumentCompanionChange? companion = null)
    {
        using DocumentEditTransaction edit = engine.BeginEditTransaction("Set width");
        if (companion is not null) edit.Enlist(companion);
        engine.CurrentDocument!.Root!.SetPropertyValue("Width", width);
        edit.Commit();
    }

    private static string? Width(SyncEngine engine) => engine.CurrentDocument!.Root!.GetPropertyValue("Width");

    private static DocumentSnapshot Snapshot(string text) => new(text, text, ImmutableDictionary<string, Guid>.Empty);

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
