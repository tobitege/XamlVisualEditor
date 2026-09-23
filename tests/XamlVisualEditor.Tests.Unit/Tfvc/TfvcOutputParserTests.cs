using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;
using XamlVisualEditor.TfvcExtension.ViewModels;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

public sealed class TfvcOutputParserTests
{
    private const string WorkspacesXml = """
        <Workspaces>
          <Workspace computer="DEV01" name="DEV01" ownerdisp="Test User" owner="0000">
            <Comment />
            <Folders>
              <WorkingFolder local="C:\src\Product" item="$/Product" />
              <WorkingFolder item="$/Product/Secret" type="Cloak" />
            </Folders>
          </Workspace>
        </Workspaces>
        """;

    private const string StatusXml = """
        <Status>
          <PendingSet computer="DEV01" name="DEV01" ownerdisp="Test User" ownership="1">
            <PendingChanges>
              <PendingChange chg="Edit" type="File" local="C:\src\Product\a.cs" item="$/Product/a.cs" ver="78998" />
              <PendingChange chg="Add Edit Encoding" type="File" local="C:\src\Product\b.cs" item="$/Product/b.cs" ver="0" lock="checkin" />
              <PendingChange chg="Rename" type="Folder" local="C:\src\Product\New" item="$/Product/New" srcitem="$/Product/Old" ver="5" lock="none" />
            </PendingChanges>
          </PendingSet>
          <PendingSet name="OTHER">
            <PendingChanges>
              <PendingChange chg="Delete" type="File" item="$/Product/c.cs" ver="3" />
            </PendingChanges>
          </PendingSet>
        </Status>
        """;

    [Fact]
    public void ParseWorkspaces_ReadsMappingsAndCloaks()
    {
        IReadOnlyList<TfvcWorkspace> workspaces = TfvcOutputParser.ParseWorkspaces(WorkspacesXml);

        TfvcWorkspace workspace = Assert.Single(workspaces);
        Assert.Equal("DEV01", workspace.Name);
        Assert.Equal("DEV01 (Test User)", workspace.DisplayName);
        Assert.Equal(2, workspace.Folders.Count);
        Assert.Equal(new TfvcWorkingFolder("$/Product", @"C:\src\Product", false), workspace.Folders[0]);
        Assert.True(workspace.Folders[1].IsCloaked);
    }

    [Fact]
    public void ParsePendingChanges_FiltersWorkspaceAndReadsAttributes()
    {
        IReadOnlyList<TfvcPendingChange> changes = TfvcOutputParser.ParsePendingChanges(StatusXml, "DEV01");

        Assert.Equal(3, changes.Count);
        Assert.Equal("$/Product/a.cs", changes[0].ServerPath);
        Assert.Equal(@"C:\src\Product\a.cs", changes[0].LocalPath);
        Assert.Equal(78998, changes[0].Version);
        Assert.Null(changes[0].LockLevel);
        Assert.Equal(["Add", "Edit", "Encoding"], changes[1].ChangeTypes);
        Assert.Equal("checkin", changes[1].LockLevel);
        Assert.True(changes[2].IsFolder);
        Assert.Equal("$/Product/Old", changes[2].SourceServerPath);
        Assert.Null(changes[2].LockLevel);
    }

    [Fact]
    public void ParsePendingChanges_WithoutFilterReturnsAllSets()
    {
        IReadOnlyList<TfvcPendingChange> changes = TfvcOutputParser.ParsePendingChanges(StatusXml);

        Assert.Equal(4, changes.Count);
        Assert.Null(changes[3].LocalPath);
    }

    [Theory]
    [InlineData("<Status />")]
    [InlineData("")]
    [InlineData("not xml")]
    public void ParsePendingChanges_EmptyOrInvalidOutputReturnsNoChanges(string output)
    {
        Assert.Empty(TfvcOutputParser.ParsePendingChanges(output));
    }

    [Theory]
    [InlineData("Changeset #4711 checked in.", 4711)]
    [InlineData("Changeset Nr. 815 wurde eingecheckt.", 815)]
    [InlineData("Änderungssatz Nr. 42 eingecheckt.", 42)]
    public void TryParseChangesetId_ReadsLocalizedOutput(string output, int expected)
    {
        Assert.Equal(expected, TfvcOutputParser.TryParseChangesetId(output));
    }

    [Fact]
    public void TryParseChangesetId_ReturnsNullWithoutChangeset()
    {
        Assert.Null(TfvcOutputParser.TryParseChangesetId("No pending changes."));
    }

    [Fact]
    public void TryParseCollectionUrl_ReadsWorkfoldHeader()
    {
        string output = "=====\nWorkspace : DEV01 (Test User)\nCollection: https://tfs.example.test/tfs/DefaultCollection\n $/Product: C:\\src\\Product";

        Assert.Equal("https://tfs.example.test/tfs/DefaultCollection", TfvcOutputParser.TryParseCollectionUrl(output));
    }

    [Fact]
    public void DiffLineParse_ClassifiesLines()
    {
        IReadOnlyList<TfvcDiffLine> lines = TfvcDiffLine.Parse("--- a.cs;C1\r\n+++ a.cs\r\n@@ -1,2 +1,2 @@\r\n same\r\n-old\r\n+new\r\n");

        Assert.Equal(
            [TfvcDiffLineKind.Header, TfvcDiffLineKind.Header, TfvcDiffLineKind.Hunk, TfvcDiffLineKind.Context, TfvcDiffLineKind.Removed, TfvcDiffLineKind.Added],
            [lines[0].Kind, lines[1].Kind, lines[2].Kind, lines[3].Kind, lines[4].Kind, lines[5].Kind]);
        Assert.Equal(6, lines.Count);
    }

    [Fact]
    public void ReadItems_ParsesItemsApiResponse()
    {
        const string json = """
            {"count":2,"value":[
              {"version":76765,"changeDate":"2026-05-29T09:43:39.633Z","path":"$/Product/src","isFolder":true},
              {"version":76700,"changeDate":"2026-05-28T08:00:00Z","path":"$/Product/src/a.cs","size":674}
            ]}
            """;
        using JsonDocument document = JsonDocument.Parse(json);

        IReadOnlyList<TfvcServerItem> items = TfvcServerClient.ReadItems(document.RootElement);

        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsFolder);
        Assert.Equal("src", items[0].Name);
        Assert.Equal(76700, items[1].Version);
        Assert.Equal("a.cs", items[1].Name);
        Assert.NotNull(items[1].ChangeDate);
    }

    [Fact]
    public void BuildChangesetLinkPatch_AddsArtifactLink()
    {
        using JsonDocument document = JsonDocument.Parse(TfvcServerClient.BuildChangesetLinkPatch(4711));

        JsonElement operation = document.RootElement[0];
        Assert.Equal("add", operation.GetProperty("op").GetString());
        Assert.Equal("/relations/-", operation.GetProperty("path").GetString());
        JsonElement value = operation.GetProperty("value");
        Assert.Equal("ArtifactLink", value.GetProperty("rel").GetString());
        Assert.Equal("vstfs:///VersionControl/Changeset/4711", value.GetProperty("url").GetString());
    }
}
