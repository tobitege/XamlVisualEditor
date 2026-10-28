using System;
using System.IO;
using XamlVisualEditor.Core;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class WorkspaceServiceFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-WorkspaceFallback-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateFallbackWorkspace_UsesSharedBuildOutput()
    {
        string productRoot = Path.Combine(_root, "ProductRoot");
        string projectDirectory = Path.Combine(productRoot, "src", "Sample.Product");
        Directory.CreateDirectory(projectDirectory);
        string projectPath = Path.Combine(projectDirectory, "Sample.Product.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Sample.Product</AssemblyName><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        string outputDirectory = Path.Combine(productRoot, "build", "SampleApp", "Debug");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory, "Sample.Product.dll");
        File.WriteAllBytes(outputPath, Array.Empty<byte>());

        WorkspaceModel workspace = WorkspaceService.CreateFallbackWorkspace(projectPath);

        ProjectModel project = Assert.Single(workspace.Projects);
        Assert.Equal(projectPath, project.ProjectPath);
        Assert.Equal(outputPath, project.OutputAssemblyPath);
        Assert.Equal("net10.0", project.TargetFramework);
        Assert.False(project.IsExecutable);
    }

    [Fact]
    public void CreateFallbackWorkspace_DoesNotTakeAnUnevaluatedTargetFramework()
    {
        Directory.CreateDirectory(_root);
        string projectPath = Path.Combine(_root, "Sample.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>$(NetCurrent)</TargetFramework></PropertyGroup></Project>");

        WorkspaceModel workspace = WorkspaceService.CreateFallbackWorkspace(projectPath);

        Assert.Null(Assert.Single(workspace.Projects).TargetFramework);
    }

    [Fact]
    public void CreateFallbackWorkspace_DoesNotTakeAConditionalTargetFramework()
    {
        Directory.CreateDirectory(_root);
        string projectPath = Path.Combine(_root, "Sample.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup Condition=\"'$(OS)' == 'Windows_NT'\"><TargetFramework>net8.0-windows</TargetFramework></PropertyGroup><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");

        WorkspaceModel workspace = WorkspaceService.CreateFallbackWorkspace(projectPath);

        Assert.Equal("net8.0", Assert.Single(workspace.Projects).TargetFramework);
    }

    [Fact]
    public void CreateFallbackWorkspace_DoesNotTakeATargetFrameworkFromAChooseBranch()
    {
        Directory.CreateDirectory(_root);
        string projectPath = Path.Combine(_root, "Sample.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><Choose><When Condition=\"'$(OS)' == 'Windows_NT'\"><PropertyGroup><TargetFramework>net8.0-windows</TargetFramework></PropertyGroup></When></Choose></Project>");

        WorkspaceModel workspace = WorkspaceService.CreateFallbackWorkspace(projectPath);

        Assert.Null(Assert.Single(workspace.Projects).TargetFramework);
    }

    [Theory]
    [InlineData("/repo/App/bin/Debug/net9.0/App.dll", "net9.0")]
    [InlineData("C:\\repo\\App\\bin\\Release\\net10.0\\win-x64\\App.dll", "net10.0")]
    [InlineData("/repo/App/bin/Debug/App.dll", null)]
    [InlineData("/repo/App/bin/Debug/x64/App.dll", null)]
    [InlineData("/repo/App/bin/Debug/net8.0-windows10.0.19041.0/App.dll", "net8.0-windows10.0.19041.0")]
    [InlineData("/repo/App/out/App.dll", null)]
    public void TargetFramework_Is_The_Folder_Below_The_Configuration(string outputPath, string? expected)
    {
        Assert.Equal(expected, WorkspaceService.TryGetTargetFrameworkFromOutputPath(outputPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
