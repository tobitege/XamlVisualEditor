using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

public sealed class TfvcClientTests
{
    [Fact]
    public async Task GetPendingChangesAsync_QueriesWholeWorkspaceAsXml()
    {
        FakeTfCommandRunner runner = new();
        runner.Enqueue(new TfCommandResult(0, """<Status><PendingSet name="DEV01"><PendingChanges><PendingChange chg="Edit" item="$/Product/a.cs" local="C:\src\Product\a.cs" type="File" ver="1" /></PendingChanges></PendingSet></Status>""", string.Empty));
        TfvcClient client = new(runner);

        IReadOnlyList<TfvcPendingChange> changes = await client.GetPendingChangesAsync(
            TfvcTestData.CollectionUrl,
            TfvcTestData.CreateWorkspace(),
            CancellationToken.None);

        Assert.Single(changes);
        IReadOnlyList<string> arguments = runner.Calls[0].Arguments;
        Assert.Equal("status", arguments[0]);
        Assert.Contains("/collection:" + TfvcTestData.CollectionUrl, arguments);
        Assert.Contains("/workspace:DEV01", arguments);
        Assert.Contains("/format:xml", arguments);
        Assert.Contains("/recursive", arguments);
        Assert.Equal("$/", arguments[^1]);
    }

    [Fact]
    public async Task CheckInAsync_PassesCommentAndItems()
    {
        FakeTfCommandRunner runner = new();
        TfvcClient client = new(runner);

        await client.CheckInAsync(TfvcTestData.CreateWorkspace(), "Fix build", ["$/Product/a.cs", "$/Product/b.cs"], CancellationToken.None);

        Assert.Equal(["checkin", "/comment:Fix build", "/noprompt", "$/Product/a.cs", "$/Product/b.cs"], runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task UpdateChangesetCommentAsync_PassesCommentCollectionAndChangeset()
    {
        FakeTfCommandRunner runner = new();
        TfvcClient client = new(runner);

        await client.UpdateChangesetCommentAsync(TfvcTestData.CollectionUrl, 4711, "Fix build", CancellationToken.None);

        Assert.Equal(
            ["changeset", "/comment:Fix build", "/collection:" + TfvcTestData.CollectionUrl, "/noprompt", "4711"],
            runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task UpdateChangesetCommentAsync_ThrowsWithOutputOnFailure()
    {
        FakeTfCommandRunner runner = new();
        runner.Enqueue(new TfCommandResult(100, string.Empty, "TF10158: not the owner."));
        TfvcClient client = new(runner);

        TfCommandException exception = await Assert.ThrowsAsync<TfCommandException>(
            () => client.UpdateChangesetCommentAsync(TfvcTestData.CollectionUrl, 4711, "Fix build", CancellationToken.None));

        Assert.Equal("changeset", exception.Command);
        Assert.Contains("TF10158", exception.Message);
    }

    [Fact]
    public async Task CheckOutAsync_ThrowsWithOutputOnFailure()
    {
        FakeTfCommandRunner runner = new();
        runner.Enqueue(new TfCommandResult(100, string.Empty, "TF10152: locked for check-out."));
        TfvcClient client = new(runner);

        TfCommandException exception = await Assert.ThrowsAsync<TfCommandException>(
            () => client.CheckOutAsync(TfvcTestData.CreateWorkspace(), [@"C:\src\Product\a.cs"], recursive: false, CancellationToken.None));

        Assert.Equal("checkout", exception.Command);
        Assert.Contains("TF10152", TfvcDisplayText.DescribeError(exception));
    }

    [Fact]
    public async Task CheckOutAsync_PassesRecursiveForFolders()
    {
        FakeTfCommandRunner runner = new();
        TfvcClient client = new(runner);

        await client.CheckOutAsync(TfvcTestData.CreateWorkspace(), [@"C:\src\Product\src"], recursive: true, CancellationToken.None);

        Assert.Equal(["checkout", "/noprompt", "/recursive", @"C:\src\Product\src"], runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task UndoAsync_RejectsOverlongCommandLines()
    {
        FakeTfCommandRunner runner = new();
        TfvcClient client = new(runner);
        List<string> items = new();
        for (int i = 0; i < 400; i++)
        {
            items.Add("$/Product/" + new string('x', 100) + i);
        }

        await Assert.ThrowsAsync<TfCommandLineTooLongException>(
            () => client.UndoAsync(TfvcTestData.CreateWorkspace(), items, false, CancellationToken.None));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task DifferenceAsync_AcceptsExitCodeOneForDifferences()
    {
        FakeTfCommandRunner runner = new();
        runner.Enqueue(new TfCommandResult(1, "@@ -1 +1 @@", string.Empty));
        TfvcClient client = new(runner);

        TfCommandResult result = await client.DifferenceAsync(TfvcTestData.CreateWorkspace(), @"C:\src\Product\a.cs", CancellationToken.None);

        Assert.Equal("@@ -1 +1 @@", result.StandardOutput);
        Assert.Equal(["difference", "/format:unified", "/noprompt", @"C:\src\Product\a.cs"], runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task GetWorkspacesAsync_ThrowsWhenTfReportsOnlyAnError()
    {
        FakeTfCommandRunner runner = new();
        runner.Enqueue(new TfCommandResult(100, string.Empty, "TF30063: not authorized."));
        TfvcClient client = new(runner);

        await Assert.ThrowsAsync<TfCommandException>(() => client.GetWorkspacesAsync(TfvcTestData.CollectionUrl, CancellationToken.None));
    }

    [Fact]
    public void DescribeError_ReportsMissingTfExe()
    {
        string message = TfvcDisplayText.DescribeError(new TfExeNotFoundException("missing"));

        Assert.Contains("tf.exe", message);
    }

    [Fact]
    public void CombinedOutput_JoinsStreams()
    {
        TfCommandResult result = new(1, " out ", " err ");

        Assert.Equal("out" + Environment.NewLine + "err", result.CombinedOutput);
        Assert.False(result.Succeeded);
    }
}
