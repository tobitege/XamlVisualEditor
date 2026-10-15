using System.Xml.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.PreviewerHost;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignerUpdateProtocolTests
{
    [Theory]
    [InlineData("<Button xmlns='https://github.com/avaloniaui' Width='{DynamicResource Width}' />")]
    [InlineData("<Widget xmlns='using:Example' Canvas.Left='10' />")]
    public void ContextDependentValuesDoNotUseTheDirectLayoutPath(string fragment)
    {
        Assert.False(DesignerUpdateProtocol.TryGetLayoutValues(fragment, Array.Empty<string>(), out _));
    }

    [Fact]
    public async Task GeometryUpdatesStartImmediatelyAndSkipObsoleteQueuedDocuments()
    {
        TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string> latest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using XamlVisualEditor.Shell.ViewModels.DesignerDocumentViewModel document = new("PreviewUpdates.axaml", updatePreviewerAsync: async current =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
            else latest.TrySetResult(current.SyncEngine.GetCurrentText());
        });
        await document.SyncEngine.LoadAsync("<Canvas xmlns='https://github.com/avaloniaui' Width='100' />");
        void Resize(string width)
        {
            using var edit = document.SyncEngine.BeginEditTransaction("Resize");
            document.SyncEngine.CurrentDocument!.Root!.SetPropertyValue("Width", width);
            edit.Commit();
        }
        Resize("110");
        Assert.True(firstStarted.Task.IsCompleted, "Designer edits must not wait for the code-editor typing delay.");
        Resize("120");
        Resize("130");
        Resize("140");
        releaseFirst.SetResult();
        string updated = await latest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(document.SyncEngine.GetCurrentText(), updated);
        Assert.Equal("140", XElement.Parse(updated).Attribute("Width")!.Value);
        Assert.Equal(2, calls);
    }

    private static readonly Guid NodeId = Guid.NewGuid();
    private static readonly Guid InitialRevision = Guid.NewGuid();

    private static string Source(Guid revision, string width = "40", string height = "20") =>
        $"<Canvas xmlns='https://github.com/avaloniaui' xmlns:id='{DesignerUpdateProtocol.IdentityNamespace}' id:DesignNodeIdentity.RenderId='{revision}'>"
        + $"<Button id:DesignNodeIdentity.NodeId='{NodeId}' Width='{width}' Height='{height}' /></Canvas>";

    [Fact]
    public void PropertyUpdatePreservesIdentityAndCarriesOnlyChangedAttributes()
    {
        Assert.True(DesignerUpdateProtocol.TryCreateUpdates(Source(InitialRevision), Source(Guid.NewGuid(), "80"), out var updates));
        DesignerControlUpdate update = Assert.Single(updates);
        Assert.Equal(NodeId, update.NodeId);
        XElement fragment = XElement.Parse(update.Xaml);
        Assert.Equal("80", fragment.Attribute("Width")!.Value);
        Assert.Null(fragment.Attribute("Height"));
        Assert.Equal("40", XElement.Parse(update.PreviousXaml).Attribute("Width")!.Value);
    }

    [Fact]
    public void AttributeAdditionAndRemovalCarryResetsAndKeepNestedNamespaces()
    {
        string source = Source(InitialRevision);
        XElement before = XElement.Parse(source);
        XElement original = Assert.Single(before.Elements());
        original.SetAttributeValue(XNamespace.Xmlns + "local", "using:Example");
        XElement after = new(before);
        XElement changed = Assert.Single(after.Elements());
        changed.Attribute("Width")!.Remove();
        changed.SetAttributeValue("Background", "{DynamicResource Accent}");
        Assert.True(DesignerUpdateProtocol.TryCreateUpdates(before.ToString(), after.ToString(), out var updates));
        DesignerControlUpdate update = Assert.Single(updates);
        Assert.Equal(new[] { "Width" }, update.ClearedProperties);
        Assert.Equal(new[] { "Background" }, update.PreviouslyUnsetProperties);
        Assert.Equal("using:Example", XElement.Parse(update.Xaml).GetNamespaceOfPrefix("local")!.NamespaceName);
        Assert.Null(XElement.Parse(update.Xaml).Attribute("Width"));
    }

    [Theory]
    [InlineData("structure")]
    [InlineData("binding")]
    [InlineData("name")]
    [InlineData("theme")]
    public void ChangesNeedingADifferentContextRequireReload(string kind)
    {
        string old = Source(InitialRevision);
        XElement changed = XElement.Parse(old);
        XElement button = Assert.Single(changed.Elements());
        if (kind == "structure") changed.Add(new XElement(button));
        else if (kind == "binding") button.SetAttributeValue("Width", "{Binding Width}");
        else if (kind == "theme") button.SetAttributeValue(XName.Get(DesignerThemeProtocol.PropertyName, DesignerThemeProtocol.XmlNamespace), "{}request");
        else button.SetAttributeValue("Name", "Different");
        Assert.False(DesignerUpdateProtocol.TryCreateUpdates(old, changed.ToString(), out _));
    }

    [Fact]
    public void FailedUpdateRollsBackEveryTouchedInstanceAndRejectsStaleRevisions()
    {
        Dictionary<string, string> first = new() { ["Width"] = "40", ["Height"] = "20" };
        Dictionary<string, string> second = new(first);
        IncrementalPreviewUpdater updater = new();
        updater.Record(Source(InitialRevision), InitialRevision, (target, fragment) =>
        {
            var values = (Dictionary<string, string>)target;
            XElement element = XElement.Parse(fragment);
            values["Width"] = element.Attribute("Width")!.Value;
            if (ReferenceEquals(target, second) && values["Width"] == "80") throw new InvalidOperationException("Rejected setter");
        });
        Guid next = Guid.NewGuid();
        int completions = 0;
        DesignerUpdateResult result = updater.Apply(new(Guid.NewGuid(), InitialRevision, next, Source(next, "80")),
            _ => new object[] { first, second }, _ => completions++);
        Assert.False(result.Applied);
        Assert.Equal("40", first["Width"]);
        Assert.Equal("40", second["Width"]);
        Assert.Equal(0, completions);
        Assert.False(updater.Apply(new(Guid.NewGuid(), next, Guid.NewGuid(), Source(next, "90")),
            _ => new object[] { first }, _ => completions++).Applied);
    }
}
