using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using AvaloniaEdit.Document;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class LinkedDocumentTransactionTests
{
    [AvaloniaFact]
    public async Task LinkedHistoryRejectsConflictingCodeTextWithoutLosingTheUndoEntry()
    {
        string path = await CreateDocumentAsync();
        using DesignerDocumentViewModel document = new(path, new TypeMetadataService());
        await document.LoadAsync();
        TextDocument code = new("class View { int before; }");
        string before = code.Text;
        const string after = "class View { int after; }";
        using (DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Linked rename"))
        {
            edit.Enlist(new DocumentCompanionChange("View.cs", before, after, () => code.Text, value => code.Text = value));
            document.SyncEngine.CurrentDocument!.Root!.SetPropertyValue("Width", "200");
            edit.Commit();
        }
        Assert.Equal(after, code.Text);
        code.Text = "class View { int userChange; }";
        Assert.Throws<InvalidOperationException>(() => document.SyncEngine.Undo());
        Assert.Equal("200", document.SyncEngine.CurrentDocument!.Root!.GetPropertyValue("Width"));
        Assert.Equal("class View { int userChange; }", code.Text);
        code.Text = after;
        document.SyncEngine.Undo();
        Assert.Equal(before, code.Text);
        Assert.Equal("100", document.SyncEngine.CurrentDocument.Root.GetPropertyValue("Width"));
        document.SyncEngine.Redo();
        Assert.Equal(after, code.Text);
        Assert.Equal("200", document.SyncEngine.CurrentDocument.Root.GetPropertyValue("Width"));
    }

    [AvaloniaFact]
    public async Task FailedRelatedWriteRestoresEveryParticipatingDocumentAndLeavesNoUndoEntry()
    {
        string path = await CreateDocumentAsync();
        using DesignerDocumentViewModel document = new(path, new TypeMetadataService());
        await document.LoadAsync();
        TextDocument first = new("first before");
        TextDocument second = new("second before");
        using (DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Failing linked edit"))
        {
            edit.Enlist(new DocumentCompanionChange("First.cs", first.Text, "first after", () => first.Text, value => first.Text = value));
            edit.Enlist(new DocumentCompanionChange("Second.cs", second.Text, "second after", () => second.Text, value =>
            {
                second.Text = value;
                if (value == "second after") throw new InvalidOperationException("Related write failed.");
            }));
            document.SyncEngine.CurrentDocument!.Root!.SetPropertyValue("Width", "200");
            Assert.Throws<InvalidOperationException>(() => edit.Commit());
        }
        Assert.Equal("first before", first.Text);
        Assert.Equal("second before", second.Text);
        Assert.Equal("100", document.SyncEngine.CurrentDocument!.Root!.GetPropertyValue("Width"));
        document.SyncEngine.Undo();
        Assert.Equal("100", document.SyncEngine.CurrentDocument.Root.GetPropertyValue("Width"));
    }

    private static async Task<string> CreateDocumentAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "xve-linked-edit-" + Guid.NewGuid().ToString("N") + ".axaml");
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, "<Canvas xmlns=\"https://github.com/avaloniaui\" Width=\"100\" />");
        return path;
    }
}
