using System;
using System.Threading.Tasks;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class SyncEngineReplaceTextTests
{
    private const string Before = "<Grid xmlns=\"https://github.com/avaloniaui\" Width=\"100\" />";
    private const string After = "<Grid xmlns=\"https://github.com/avaloniaui\" Width=\"200\" />";

    [Fact]
    public async Task ReplaceText_Records_One_Step_That_Undoes_And_Redoes_The_Related_Documents()
    {
        using SyncEngine engine = await CreateEngineAsync();
        string code = "class View { int before; }";
        DocumentCompanionChange change = new("View.cs", code, "class View { int after; }", () => code, value => code = value);

        engine.ReplaceText(Before, After, [change], XamlVisualEditor.Core.SyncSource.DesignSurface);

        Assert.Equal(After, engine.CurrentText);
        Assert.Equal("200", engine.CurrentDocument!.Root!.GetPropertyValue("Width"));
        Assert.Equal("class View { int after; }", code);
        Assert.Equal(1, engine.UndoRedo.UndoCount);

        engine.Undo();
        Assert.Equal(Before, engine.CurrentText);
        Assert.Equal("class View { int before; }", code);

        engine.Redo();
        Assert.Equal(After, engine.CurrentText);
        Assert.Equal("class View { int after; }", code);
    }

    [Fact]
    public async Task ReplaceText_Refuses_A_Changed_Text_Or_An_Unfinished_Edit_Without_Changing_Anything()
    {
        using SyncEngine engine = await CreateEngineAsync();
        string code = "before";
        DocumentCompanionChange change = new("View.cs", code, "after", () => code, value => code = value);

        InvalidOperationException changed = Assert.Throws<InvalidOperationException>(() =>
            engine.ReplaceText(After, Before, [change], XamlVisualEditor.Core.SyncSource.DesignSurface));
        Assert.Equal(UiStrings.DesignerTextChangedBeforeEdit, changed.Message);

        using (engine.BeginEditTransaction("Unfinished"))
        {
            Assert.False(engine.CanReplaceText);
            Assert.Throws<InvalidOperationException>(() =>
                engine.ReplaceText(Before, After, [change], XamlVisualEditor.Core.SyncSource.DesignSurface));
        }

        Assert.True(engine.CanReplaceText);
        Assert.Equal(Before, engine.CurrentText);
        Assert.Equal("before", code);
        Assert.False(engine.UndoRedo.CanUndo);
    }

    [Fact]
    public async Task ReplaceText_Commits_Pending_Ast_Changes_As_Their_Own_Step_First()
    {
        using SyncEngine engine = await CreateEngineAsync();
        engine.CurrentDocument!.Root!.SetPropertyValue("Height", "50");

        Assert.Throws<InvalidOperationException>(() =>
            engine.ReplaceText(Before, After, [], XamlVisualEditor.Core.SyncSource.DesignSurface));

        Assert.Contains("Height=\"50\"", engine.CurrentText);
        Assert.Equal(1, engine.UndoRedo.UndoCount);
    }

    private static async Task<SyncEngine> CreateEngineAsync()
    {
        SyncEngine engine = new(new XamlParsingService(), new XamlSerializationService(), new AstNodeMap());
        await engine.LoadAsync(Before);
        return engine;
    }
}
