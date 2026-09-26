using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class SolutionExplorerProjectMarkerTests
{
    [Fact]
    public void ProjectToolTip_DescribesStartupAndExecutableProjects()
    {
        SolutionExplorerNodeViewModel executable = CreateProjectNode(isExecutable: true);

        Assert.Equal(UiStrings.ExecutableProjectToolTip, executable.ProjectToolTip);

        executable.IsStartupProject = true;
        Assert.Equal(UiStrings.StartupProjectToolTip, executable.ProjectToolTip);
    }

    [Fact]
    public void ProjectToolTip_IsEmptyForLibrariesAndOtherNodes()
    {
        SolutionExplorerNodeViewModel library = CreateProjectNode(isExecutable: false);
        SolutionExplorerNodeViewModel folder = new("Views", null, SolutionExplorerNodeKind.Folder);

        Assert.Null(library.ProjectToolTip);
        Assert.Null(folder.ProjectToolTip);
    }

    private static SolutionExplorerNodeViewModel CreateProjectNode(bool isExecutable)
    {
        ProjectModel project = new()
        {
            Name = "App", ProjectPath = "App.csproj", IsExecutable = isExecutable,
            Files = Array.Empty<ProjectFileModel>(), XamlFiles = Array.Empty<XamlFileModel>(),
            References = Array.Empty<AssemblyReference>()
        };
        return new SolutionExplorerNodeViewModel(project.Name, null, SolutionExplorerNodeKind.Project, project.ProjectPath, project);
    }
}
