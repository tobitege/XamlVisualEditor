using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class GitExecutableLocatorTests
{
    private static readonly string FirstFolder = Path.Combine(Path.GetTempPath(), "first");
    private static readonly string SecondFolder = Path.Combine(Path.GetTempPath(), "second");
    private static readonly string Installed = Path.Combine(Path.GetTempPath(), "Git", "cmd", "git.exe");

    private static GitExecutableLocator CreateLocator(string? pathVariable, params string[] existingFiles) =>
        new(
            file => existingFiles.Contains(file),
            () => pathVariable,
            () => new[] { Installed },
            "git.exe");

    [Fact]
    public void Returns_the_first_PATH_folder_that_contains_git()
    {
        string path = string.Join(Path.PathSeparator, FirstFolder, "\"" + SecondFolder + "\"");
        GitExecutableLocator locator = CreateLocator(path, Path.Combine(SecondFolder, "git.exe"), Installed);

        Assert.Equal(Path.Combine(SecondFolder, "git.exe"), locator.Locate()?.Path);
    }

    [Fact]
    public void Skips_relative_PATH_entries()
    {
        string path = string.Join(Path.PathSeparator, ".", "relative");
        GitExecutableLocator locator = CreateLocator(path, Path.Combine(".", "git.exe"), Path.Combine("relative", "git.exe"));

        Assert.Null(locator.Locate());
    }

    [Fact]
    public void Falls_back_to_the_installation_folder()
    {
        GitExecutableLocator locator = CreateLocator(FirstFolder, Installed);

        Assert.Equal(Installed, locator.Locate()?.Path);
    }

    [Theory]
    [InlineData("cmd", "mingw64")]
    [InlineData("bin", "mingw64")]
    [InlineData("CMD", "mingw32")]
    [InlineData("cmd", "clangarm64")]
    public void A_Git_for_Windows_launcher_is_replaced_by_the_git_it_starts_with_its_folders_on_PATH(string launcherFolder, string environment)
    {
        string root = Path.Combine(Path.GetTempPath(), "PortableGit");
        string launcher = Path.Combine(root, launcherFolder, "git.exe");
        string real = Path.Combine(root, environment, "bin", "git.exe");
        GitExecutableLocator locator = CreateLocator(Path.Combine(root, launcherFolder), launcher, real);

        GitExecutable? located = locator.Locate();

        Assert.NotNull(located);
        Assert.Equal(real, located.Path);
        Assert.NotNull(located.Layout);
        Assert.Equal(new GitForWindowsLayout(root, environment), located.Layout);
        Assert.Equal([Path.Combine(root, environment, "bin"), Path.Combine(root, "usr", "bin")], located.Layout.SearchPathFolders);
        Assert.Equal(environment.ToUpperInvariant(), located.Layout.MsysEnvironment);
    }

    [Fact]
    public void A_configured_executable_path_of_a_launcher_is_resolved_too()
    {
        string root = Path.Combine(Path.GetTempPath(), "PortableGit");
        string launcher = Path.Combine(root, "cmd", "git.exe");
        string real = Path.Combine(root, "mingw64", "bin", "git.exe");
        GitProcessRunner runner = new(
            new GitProcessOptions { ExecutablePath = launcher },
            CreateLocator(null, launcher, real));

        Assert.Equal(real, runner.ExecutablePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_scoop_shim_is_resolved_to_the_git_its_launcher_starts(bool quotedPath)
    {
        string shims = Path.Combine(Path.GetTempPath(), "scoop", "shims");
        string root = Path.Combine(Path.GetTempPath(), "scoop", "apps", "git", "current");
        string shim = Path.Combine(shims, "git.exe");
        string shimFile = Path.Combine(shims, "git.shim");
        string launcher = Path.Combine(root, "cmd", "git.exe");
        string real = Path.Combine(root, "mingw64", "bin", "git.exe");
        string[] existing = [shim, shimFile, launcher, real];
        // Older scoop versions write the path without quotes.
        string pathLine = quotedPath ? "path = \"" + launcher + "\"" : "path = " + launcher;
        GitExecutableLocator locator = new(
            file => existing.Contains(file),
            () => shims,
            () => Array.Empty<string>(),
            "git.exe",
            file => file == shimFile ? pathLine + "\r\n" : null);

        GitExecutable? located = locator.Locate();

        Assert.NotNull(located);
        Assert.Equal(real, located.Path);
        Assert.Equal(new GitForWindowsLayout(root, "mingw64"), located.Layout);
    }

    [Theory]
    [InlineData("args = --no-pager", true)]
    [InlineData("path = relative\\git.exe", true)]
    [InlineData("path = C:\\missing\\git.exe", true)]
    [InlineData("", false)]
    public void A_scoop_shim_with_arguments_or_without_an_existing_program_is_kept(string extraLine, bool keepsShim)
    {
        string shims = Path.Combine(Path.GetTempPath(), "scoop", "shims");
        string shim = Path.Combine(shims, "git.exe");
        string shimFile = Path.Combine(shims, "git.shim");
        // A program outside a Git for Windows layout.
        string program = Path.Combine(Path.GetTempPath(), "tools", "git.exe");
        string[] existing = [shim, shimFile, program];
        GitExecutableLocator locator = new(
            file => existing.Contains(file),
            () => shims,
            () => Array.Empty<string>(),
            "git.exe",
            file => file == shimFile ? "path = \"" + program + "\"\n" + extraLine + "\n" : null);

        GitExecutable? located = locator.Locate();

        Assert.NotNull(located);
        Assert.Equal(keepsShim ? shim : program, located.Path);
        Assert.Null(located.Layout);
    }

    [Fact]
    public void The_default_installation_path_is_resolved_like_a_PATH_entry()
    {
        string real = Path.Combine(Path.GetTempPath(), "Git", "mingw64", "bin", "git.exe");
        GitExecutableLocator locator = CreateLocator(null, Installed, real);

        Assert.Equal(real, locator.Locate()?.Path);
    }

    [Fact]
    public void Git_without_a_launcher_layout_keeps_its_path_and_PATH()
    {
        string real = Path.Combine(Path.GetTempPath(), "Git", "mingw64", "bin", "git.exe");
        string launcherWithoutGit = Path.Combine(Path.GetTempPath(), "Other", "cmd", "git.exe");

        GitExecutable? direct = CreateLocator(Path.GetDirectoryName(real), real).Locate();
        GitExecutable? launcher = CreateLocator(Path.GetDirectoryName(launcherWithoutGit), launcherWithoutGit).Locate();

        Assert.NotNull(direct);
        Assert.Equal(real, direct.Path);
        Assert.Null(direct.Layout);
        Assert.NotNull(launcher);
        Assert.Equal(launcherWithoutGit, launcher.Path);
        Assert.Null(launcher.Layout);
    }

    [Fact]
    public void Returns_null_without_any_git()
    {
        Assert.Null(CreateLocator(null).Locate());
    }

    [Theory]
    [InlineData("git version 2.45.1.windows.1", 2, 45, 1)]
    [InlineData("git version 2.26.0\n", 2, 26, 0)]
    [InlineData("git version 2.39.3 (Apple Git-146)", 2, 39, 3)]
    public void Parses_the_version_output(string output, int major, int minor, int build)
    {
        Assert.True(GitExecutableLocator.TryParseVersion(output, out Version? version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void Rejects_output_that_is_no_version()
    {
        Assert.False(GitExecutableLocator.TryParseVersion("git: command not found", out _));
        Assert.False(GitExecutableLocator.TryParseVersion("git version abc", out _));
    }

    [Fact]
    public void Versions_below_the_minimum_are_older()
    {
        Assert.True(GitExecutableLocator.TryParseVersion("git version 2.25.5", out Version? version));
        Assert.True(version < GitExecutableLocator.MinimumVersion);
        Assert.True(GitExecutableLocator.TryParseVersion("git version 2.26.0", out Version? minimum));
        Assert.False(minimum < GitExecutableLocator.MinimumVersion);
    }
}
