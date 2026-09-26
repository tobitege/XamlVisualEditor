using System;
using System.IO;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class WorkspaceSessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xve-sessions-" + Guid.NewGuid().ToString("N"));

    public WorkspaceSessionStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Save_PersistsTheSessionPerWorkspace()
    {
        string file = Path.Combine(_directory, "sessions.json");
        WorkspaceSession session = new()
        {
            StartupProjectPath = @"C:\work\App\App.csproj",
            StartupTargetFramework = "net10.0",
            OpenDocuments = new[] { @"C:\work\App\A.axaml", @"C:\scratch\B.axaml" },
            ActiveDocument = @"C:\scratch\B.axaml"
        };

        new WorkspaceSessionStore(file).Save(@"C:\work\App.slnx", session);
        WorkspaceSession? loaded = new WorkspaceSessionStore(file).Load(@"C:\work\App.slnx");

        Assert.NotNull(loaded);
        Assert.Equal(session.StartupProjectPath, loaded.StartupProjectPath);
        Assert.Equal(session.StartupTargetFramework, loaded.StartupTargetFramework);
        Assert.Equal(session.OpenDocuments, loaded.OpenDocuments);
        Assert.Equal(session.ActiveDocument, loaded.ActiveDocument);
        Assert.Null(new WorkspaceSessionStore(file).Load(@"C:\work\Other.slnx"));
    }

    [Fact]
    public void Save_ReplacesTheSessionOfTheSameWorkspace()
    {
        WorkspaceSessionStore store = new();
        store.Save("Workspace.slnx", new WorkspaceSession { OpenDocuments = new[] { "Old.axaml" } });

        store.Save("Workspace.slnx", new WorkspaceSession { OpenDocuments = new[] { "New.axaml" } });

        Assert.Equal(new[] { "New.axaml" }, store.Load("Workspace.slnx")?.OpenDocuments);
    }

    [Fact]
    public void Save_KeepsTheFiftyMostRecentWorkspaces()
    {
        WorkspaceSessionStore store = new();
        for (int index = 0; index < 51; index++)
        {
            store.Save($"Workspace{index}.slnx", new WorkspaceSession());
        }

        Assert.Null(store.Load("Workspace0.slnx"));
        Assert.NotNull(store.Load("Workspace1.slnx"));
        Assert.NotNull(store.Load("Workspace50.slnx"));
    }

    [Fact]
    public void Load_IgnoresAnUnreadableFile()
    {
        string file = Path.Combine(_directory, "broken.json");
        File.WriteAllText(file, "{ not json");

        Assert.Null(new WorkspaceSessionStore(file).Load("Workspace.slnx"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
