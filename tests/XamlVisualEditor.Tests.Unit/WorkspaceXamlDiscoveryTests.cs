using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class WorkspaceXamlDiscoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xve-xaml-discovery-" + Guid.NewGuid().ToString("N"));

    public WorkspaceXamlDiscoveryTests()
    {
        Directory.CreateDirectory(_directory);
        CreateFile("readme.txt");
        CreateFile(Path.Combine("Views", "Main.axaml"));
        CreateFile(Path.Combine("Old", "Legacy.axaml"));
        CreateFile(Path.Combine("bin", "Debug", "Copied.axaml"));
    }

    [Fact]
    public void Sdk_Project_Keeps_Its_Default_Xaml_Files_Next_To_Explicit_Items()
    {
        string project = CreateProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <None Update="readme.txt" CopyToOutputDirectory="PreserveNewest" />
                <AvaloniaXaml Remove="Old\**" />
              </ItemGroup>
            </Project>
            """);

        string[] files = Discover(project);

        Assert.Contains(Path.Combine("Views", "Main.axaml"), files);
        Assert.Contains("readme.txt", files);
        Assert.DoesNotContain(Path.Combine("Old", "Legacy.axaml"), files);
        Assert.DoesNotContain(Path.Combine("bin", "Debug", "Copied.axaml"), files);
    }

    [Theory]
    [InlineData("EnableDefaultItems")]
    [InlineData("EnableDefaultAvaloniaItems")]
    public void Sdk_Project_Without_Default_Items_Lists_Only_Its_Explicit_Items(string property)
    {
        string project = CreateProject($"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><{property}>false</{property}></PropertyGroup>
              <ItemGroup><None Update="readme.txt" /></ItemGroup>
            </Project>
            """);

        Assert.Equal(["readme.txt"], Discover(project));
    }

    [Fact]
    public void Classic_Project_Without_Xaml_Items_Falls_Back_To_The_Folder()
    {
        string project = CreateProject("""
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup><Compile Include="Program.cs" /></ItemGroup>
            </Project>
            """);

        Assert.Contains(Path.Combine("Views", "Main.axaml"), Discover(project));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string[] Discover(string project) =>
        new WorkspaceService().LoadXamlFromProjectFile(project).Select(file => file.RelativePath).Distinct().ToArray();

    private string CreateProject(string content)
    {
        string path = Path.Combine(_directory, "App.csproj");
        File.WriteAllText(path, content);
        return path;
    }

    private void CreateFile(string relativePath)
    {
        string path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }
}
