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

        Assert.Equal(Path.Combine(SecondFolder, "git.exe"), locator.Locate());
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

        Assert.Equal(Installed, locator.Locate());
    }

    [Fact]
    public void Returns_null_without_any_git()
    {
        Assert.Null(CreateLocator(null).Locate());
    }

    [Theory]
    [InlineData("git version 2.45.1.windows.1", 2, 45, 1)]
    [InlineData("git version 2.23.0\n", 2, 23, 0)]
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
        Assert.True(GitExecutableLocator.TryParseVersion("git version 2.22.5", out Version? version));
        Assert.True(version < GitExecutableLocator.MinimumVersion);
    }
}
