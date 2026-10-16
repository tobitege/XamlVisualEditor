using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

public sealed class TfvcClientTests
{
    // Commands without /workspace run in an existing mapped folder, so the workspace maps the temporary folder.
    private static readonly TfvcWorkspace MappedWorkspace = TfvcTestData.CreateWorkspace(localRoot: Path.GetTempPath());

    [Fact]
    public async Task ChangeCommands_RunInTheMappedFolderAndFailWithoutOne()
    {
        FakeTfCommandRunner runner = new();
        TfvcClient client = new(runner);
        TfvcWorkspace unmapped = TfvcTestData.CreateWorkspace(localRoot: Path.Combine(Path.GetTempPath(), "tfvc-missing-" + Guid.NewGuid().ToString("N")));

        await client.CheckOutAsync(MappedWorkspace, ["$/Product/a.cs"], recursive: false, CancellationToken.None);
        TfvcWorkspaceFolderNotFoundException exception = await Assert.ThrowsAsync<TfvcWorkspaceFolderNotFoundException>(
            () => client.CheckInAsync(unmapped, "Update", ["$/Product/a.cs"], CancellationToken.None));

        Assert.Equal(MappedWorkspace.GetWorkingDirectory(), Assert.Single(runner.Calls).WorkingDirectory);
        Assert.Contains("DEV01", TfvcDisplayText.DescribeError(exception));
    }

    [Fact]
    public async Task AddAsync_SplitsLongItemListsIntoSeveralCalls()
    {
        FakeTfCommandRunner runner = new();
        TfvcClient client = new(runner);
        List<string> items = new();
        for (int i = 0; i < 400; i++)
        {
            items.Add(Path.Combine(Path.GetTempPath(), new string('x', 100) + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        await client.AddAsync(MappedWorkspace, items, CancellationToken.None);

        Assert.True(runner.Calls.Count > 1);
        List<string> added = new();
        foreach ((string WorkingDirectory, IReadOnlyList<string> Arguments) call in runner.Calls)
        {
            Assert.Equal(["add", "/noprompt"], call.Arguments.Take(2));
            Assert.True(TfCommandRunner.EstimateCommandLineLength(call.Arguments) <= TfCommandRunner.MAX_COMMAND_LINE_LENGTH);
            added.AddRange(call.Arguments.Skip(2));
        }

        Assert.Equal(items, added);
    }

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
    public async Task CheckInAsync_PassesCommentFileAndItems()
    {
        FakeTfCommandRunner runner = new();
        CommentFileProbe probe = new(runner, argumentIndex: 1);
        TfvcClient client = new(runner);

        await client.CheckInAsync(MappedWorkspace, "@Max: layout fixed\r\nÄnderung für Grüße", ["$/Product/a.cs", "$/Product/b.cs"], CancellationToken.None);

        IReadOnlyList<string> arguments = runner.Calls[0].Arguments;
        Assert.Equal(["checkin", "/comment:@" + probe.FilePath, "/noprompt", "$/Product/a.cs", "$/Product/b.cs"], arguments);
        Assert.Equal("@Max: layout fixed\r\nÄnderung für Grüße", probe.Content);
        Assert.True(probe.HasByteOrderMark);
        Assert.False(File.Exists(probe.FilePath));
    }

    [Fact]
    public async Task CheckInAsync_DeletesCommentFileWhenTfFails()
    {
        FakeTfCommandRunner runner = new();
        runner.Enqueue(new TfCommandResult(100, string.Empty, "TF10139: conflict."));
        CommentFileProbe probe = new(runner, argumentIndex: 1);
        TfvcClient client = new(runner);

        await Assert.ThrowsAsync<TfCommandException>(
            () => client.CheckInAsync(MappedWorkspace, "Fix build", ["$/Product/a.cs"], CancellationToken.None));

        Assert.Equal("Fix build", probe.Content);
        Assert.False(File.Exists(probe.FilePath));
    }

    [Fact]
    public async Task ShelveAsync_PassesCommentFileOnlyWithComment()
    {
        FakeTfCommandRunner runner = new();
        CommentFileProbe probe = new(runner, argumentIndex: 3);
        TfvcClient client = new(runner);

        await client.ShelveAsync(MappedWorkspace, "Nightly", "@Max: draft", ["$/Product/a.cs"], CancellationToken.None);
        probe.Stop();
        await client.ShelveAsync(MappedWorkspace, "Nightly", "  ", ["$/Product/a.cs"], CancellationToken.None);

        Assert.Equal(["shelve", "Nightly", "/noprompt", "/comment:@" + probe.FilePath, "$/Product/a.cs"], runner.Calls[0].Arguments);
        Assert.Equal("@Max: draft", probe.Content);
        Assert.False(File.Exists(probe.FilePath));
        Assert.Equal(["shelve", "Nightly", "/noprompt", "$/Product/a.cs"], runner.Calls[1].Arguments);
    }

    [Fact]
    public async Task UpdateChangesetCommentAsync_PassesCommentFileCollectionAndChangeset()
    {
        FakeTfCommandRunner runner = new();
        CommentFileProbe probe = new(runner, argumentIndex: 1);
        TfvcClient client = new(runner);

        await client.UpdateChangesetCommentAsync(TfvcTestData.CollectionUrl, 4711, "@Max: revised", CancellationToken.None);

        Assert.Equal(
            ["changeset", "/comment:@" + probe.FilePath, "/collection:" + TfvcTestData.CollectionUrl, "/noprompt", "4711"],
            runner.Calls[0].Arguments);
        Assert.Equal("@Max: revised", probe.Content);
        Assert.False(File.Exists(probe.FilePath));
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
            () => client.CheckOutAsync(MappedWorkspace, [@"C:\src\Product\a.cs"], recursive: false, CancellationToken.None));

        Assert.Equal("checkout", exception.Command);
        Assert.Contains("TF10152", TfvcDisplayText.DescribeError(exception));
    }

    [Fact]
    public async Task CheckOutAsync_PassesRecursiveForFolders()
    {
        FakeTfCommandRunner runner = new();
        TfvcClient client = new(runner);

        await client.CheckOutAsync(MappedWorkspace, [@"C:\src\Product\src"], recursive: true, CancellationToken.None);

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
            () => client.UndoAsync(MappedWorkspace, items, false, CancellationToken.None));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task DifferenceAsync_AcceptsExitCodeOneForDifferences()
    {
        FakeTfCommandRunner runner = new();
        runner.Enqueue(new TfCommandResult(1, "@@ -1 +1 @@", string.Empty));
        TfvcClient client = new(runner);

        TfCommandResult result = await client.DifferenceAsync(MappedWorkspace, @"C:\src\Product\a.cs", CancellationToken.None);

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

    // Reads the comment file while the command runs; the client deletes the file afterwards.
    private sealed class CommentFileProbe
    {
        private const string COMMENT_PREFIX = "/comment:@";

        private readonly FakeTfCommandRunner _runner;

        public CommentFileProbe(FakeTfCommandRunner runner, int argumentIndex)
        {
            _runner = runner;
            runner.OnRun = arguments =>
            {
                string argument = arguments[argumentIndex];
                Assert.StartsWith(COMMENT_PREFIX, argument);
                FilePath = argument.Substring(COMMENT_PREFIX.Length);
                byte[] bytes = File.ReadAllBytes(FilePath);
                HasByteOrderMark = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                Content = File.ReadAllText(FilePath, Encoding.UTF8);
            };
        }

        public string FilePath { get; private set; } = string.Empty;

        public string? Content { get; private set; }

        public bool HasByteOrderMark { get; private set; }

        public void Stop() => _runner.OnRun = null;
    }
}
