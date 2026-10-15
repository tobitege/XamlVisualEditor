using System.ComponentModel;
using System.Diagnostics;
using XamlVisualEditor.Core;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class GitServiceTests
{
    [Fact]
    public async Task Discard_keeps_a_staged_new_file_and_restores_its_staged_content()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repo = Path.Combine(Path.GetTempPath(), "xve-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Assert.SkipUnless(TryRunGit(repo, "init", "-q"), "git is not available.");
            RunGit(repo, "config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(repo, "tracked.txt"), "tracked\n");
            RunGit(repo, "add", "--", "tracked.txt");
            RunGit(repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-q", "-m", "init");

            string newFile = Path.Combine(repo, "new.txt");
            File.WriteAllText(newFile, "staged\n");
            RunGit(repo, "add", "--", "new.txt");
            File.WriteAllText(newFile, "staged\nedited\n");

            GitService service = new();
            GitRepositoryStatus before = await service.GetStatusAsync(repo, ct);
            GitFileChange added = Assert.Single(before.Changes, change => change.Path == "new.txt");
            Assert.Equal(GitChangeKind.Added, added.IndexStatus);
            Assert.Equal(GitChangeKind.Modified, added.WorkTreeStatus);

            await service.DiscardAsync(repo, new[] { "new.txt" }, ct);

            Assert.True(File.Exists(newFile));
            Assert.Equal("staged\n", File.ReadAllText(newFile));
            GitRepositoryStatus after = await service.GetStatusAsync(repo, ct);
            GitFileChange staged = Assert.Single(after.Changes, change => change.Path == "new.txt");
            Assert.Equal(GitChangeKind.Added, staged.IndexStatus);
            Assert.Equal(GitChangeKind.None, staged.WorkTreeStatus);
        }
        finally
        {
            DeleteRepository(repo);
        }
    }

    private static void RunGit(string workingDirectory, params string[] args) =>
        Assert.True(TryRunGit(workingDirectory, args), "git " + string.Join(' ', args) + " failed.");

    private static bool TryRunGit(string workingDirectory, params string[] args)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args) startInfo.ArgumentList.Add(arg);

        try
        {
            using Process process = Process.Start(startInfo)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static void DeleteRepository(string path)
    {
        try
        {
            // Git marks object files read-only.
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
