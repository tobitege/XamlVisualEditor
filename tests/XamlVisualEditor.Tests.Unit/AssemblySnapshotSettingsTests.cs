using System.Text.Json;
using XamlVisualEditor.Core;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class AssemblySnapshotSettingsTests
{
    [Fact]
    public void MissingSnapshotOutput_DoesNotDiscoverOrSelectLiveProjectAssemblies()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-snapshot-discovery-" + Guid.NewGuid().ToString("N"));
        string live = Path.Combine(root, "bin", "Debug", "net10.0");
        string snapshot = Path.Combine(root, "snapshot");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(snapshot);
        File.WriteAllBytes(Path.Combine(live, "Sample.dll"), new byte[] { 1 });
        string dependency = Path.Combine(snapshot, "Controls.dll");
        File.WriteAllBytes(dependency, new byte[] { 2 });
        ProjectModel project = new()
        {
            Name = "Sample", ProjectPath = Path.Combine(root, "Sample.csproj"),
            AssemblySnapshotDirectory = snapshot, TargetFramework = "net10.0",
            XamlFiles = Array.Empty<XamlFileModel>(), Files = Array.Empty<ProjectFileModel>(),
            References = Array.Empty<AssemblyReference>()
        };

        Assert.Equal(Path.Combine(snapshot, "Sample.dll"), ProjectSelection.ResolveTargetAssemblyPath(project));
        Assert.Equal(dependency, Assert.Single(WorkspaceAssemblyDiscovery.FindProjectOutputs(project)));
    }

    [Fact]
    public void Snapshot_RedirectsOutputAndReferences_WithoutFallingBackToLiveFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-assembly-snapshot-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace");
        string live = Path.Combine(workspace, "build");
        string snapshot = Path.Combine(root, "snapshot");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(Path.Combine(snapshot, "de"));
        string config = Path.Combine(root, "assembly-snapshots.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new[]
        {
            new AssemblySnapshotSettings
            {
                WorkspaceRoot = workspace, SourceDirectory = live, SnapshotDirectory = snapshot,
                PreviewApplication = "Sample.dll"
            }
        }));
        string copiedAssembly = Path.Combine(snapshot, "Sample.dll");
        File.WriteAllBytes(copiedAssembly, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(snapshot, "de", "Sample.resources.dll"), new byte[] { 4, 5, 6 });
        string liveAssembly = Path.Combine(live, "Sample.dll");
        File.WriteAllBytes(liveAssembly, new byte[] { 7, 8, 9 });

        AssemblySnapshotSettings settings = Assert.IsType<AssemblySnapshotSettings>(
            AssemblySnapshotSettings.FindForWorkspace(Path.Combine(workspace, "Sample.csproj"), config));

        Assert.Equal(copiedAssembly, settings.ResolveOutputAssembly(liveAssembly, "Sample"));
        Assert.Equal(copiedAssembly, settings.ResolvePreviewApplication());
        Assert.Equal(copiedAssembly, settings.ResolveAssemblyPath(liveAssembly));
        Assert.Equal(copiedAssembly, settings.ResolveAssemblyPath(Path.Combine(root, "packages", "Sample.dll")));
        Assert.Equal(Path.Combine(snapshot, "de", "Sample.resources.dll"),
            settings.ResolveAssemblyPath(Path.Combine(live, "de", "Sample.resources.dll")));
        Assert.Equal(snapshot + Path.DirectorySeparatorChar, settings.CreateMSBuildProperties()["OutDir"]);
        Assert.Equal(snapshot, settings.CreateMSBuildProperties()["ReferencePath"]);
        Assert.Null(AssemblySnapshotSettings.FindForWorkspace(Path.Combine(root, "workspace-other", "Other.csproj"), config));
        settings.EnsureNoLiveReferences(new[] { copiedAssembly });
        Assert.Throws<InvalidOperationException>(() => settings.EnsureNoLiveReferences(new[] { liveAssembly }));
        File.WriteAllBytes(Path.Combine(live, "Missing.dll"), new byte[] { 1 });
        Assert.Throws<FileNotFoundException>(() => settings.ResolveAssemblyPath(Path.Combine(live, "Missing.dll")));
    }
}
