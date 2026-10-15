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
            InitRepository(repo, "tracked.txt");

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

    [Fact]
    public async Task Discard_treats_glob_characters_in_a_path_literally()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repo = Path.Combine(Path.GetTempPath(), "xve-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            InitRepository(repo, "Item[1].xaml", "Item1.xaml");
            string bracketFile = Path.Combine(repo, "Item[1].xaml");
            string plainFile = Path.Combine(repo, "Item1.xaml");
            File.WriteAllText(bracketFile, "edited\n");
            File.WriteAllText(plainFile, "edited\n");

            await new GitService().DiscardAsync(repo, new[] { "Item[1].xaml" }, ct);

            Assert.Equal("Item[1].xaml\n", File.ReadAllText(bracketFile));
            Assert.Equal("edited\n", File.ReadAllText(plainFile));
        }
        finally
        {
            DeleteRepository(repo);
        }
    }

    [Fact]
    public async Task Failing_git_commands_are_reported_to_the_caller()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string repo = Path.Combine(Path.GetTempPath(), "xve-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            InitRepository(repo, "tracked.txt");
            GitService service = new();

            InvalidOperationException discard = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.DiscardAsync(repo, new[] { "missing.xaml" }, ct));
            Assert.Contains("missing.xaml", discard.Message);
            InvalidOperationException stage = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StageAsync(repo, new[] { "missing.xaml" }, ct));
            Assert.Contains("missing.xaml", stage.Message);
        }
        finally
        {
            DeleteRepository(repo);
        }
    }

    /// <summary>
    /// Creates a repository with one commit that contains each file with its own name as content.
    /// </summary>
    private static void InitRepository(string repo, params string[] files)
    {
        Assert.SkipUnless(TryRunGit(repo, "init", "-q"), "git is not available.");
        RunGit(repo, "config", "core.autocrlf", "false");
        foreach (string file in files)
        {
            File.WriteAllText(Path.Combine(repo, file), file + "\n");
        }

        List<string> addArgs = new() { "add", "--" };
        addArgs.AddRange(files);
        RunGit(repo, addArgs.ToArray());
        RunGit(repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-q", "-m", "init");
    }

    private static void RunGit(string workingDirectory, params string[] args) =>
        Assert.True(TryRunGit(workingDirectory, args), "git " + string.Join(' ', args) + " failed.");

    /// <summary>
    /// Runs git without the machine's global and system configuration, so settings such as
    /// commit.gpgsign or core.hooksPath cannot break the test repository setup.
    /// </summary>
    private static bool TryRunGit(string workingDirectory, params string[] args)
    {
        string globalConfig = GetGlobalConfigPath(workingDirectory);
        if (!File.Exists(globalConfig)) File.WriteAllText(globalConfig, string.Empty);

        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = globalConfig;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
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

    private static string GetGlobalConfigPath(string repo) =>
        Path.TrimEndingDirectorySeparator(repo) + ".gitconfig";

    private static void DeleteRepository(string path)
    {
        try
        {
            File.Delete(GetGlobalConfigPath(path));
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
