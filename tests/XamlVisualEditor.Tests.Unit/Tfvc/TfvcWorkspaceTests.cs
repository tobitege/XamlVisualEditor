using System.Collections.Generic;
using Xunit;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

public sealed class TfvcWorkspaceTests
{
    [Fact]
    public void TryGetServerPath_MapsNestedLocalPath()
    {
        TfvcWorkspace workspace = TfvcTestData.CreateWorkspace();

        Assert.Equal("$/Product/src/App.cs", workspace.TryGetServerPath(@"C:\src\Product\src\App.cs"));
        Assert.Equal("$/Product", workspace.TryGetServerPath(@"C:\src\Product\"));
    }

    [Fact]
    public void TryGetServerPath_RejectsUnmappedAndCloakedPaths()
    {
        TfvcWorkspace workspace = TfvcTestData.CreateWorkspace();

        Assert.Null(workspace.TryGetServerPath(@"C:\src\ProductOther\a.cs"));
        Assert.Null(workspace.TryGetServerPath(@"C:\src\Product\Secret\key.txt"));
        Assert.False(workspace.ContainsLocalPath(@"D:\elsewhere\a.cs"));
    }

    [Fact]
    public void TryGetLocalPath_UsesLongestMapping()
    {
        TfvcWorkspace workspace = new(
            "DEV01",
            null,
            null,
            [
                new TfvcWorkingFolder("$/Product", @"C:\src\Product", false),
                new TfvcWorkingFolder("$/Product/Tools", @"D:\tools", false)
            ]);

        Assert.Equal(@"D:\tools\build.cmd", workspace.TryGetLocalPath("$/Product/Tools/build.cmd"));
        Assert.Equal(@"C:\src\Product\src\a.cs", workspace.TryGetLocalPath("$/Product/src/a.cs"));
        Assert.Null(workspace.TryGetLocalPath("$/Other/a.cs"));
    }

    [Fact]
    public void TryGetLocalPath_ReturnsNullForCloakedFolder()
    {
        Assert.Null(TfvcTestData.CreateWorkspace().TryGetLocalPath("$/Product/Secret/key.txt"));
    }

    [Theory]
    [InlineData("$/", "$/")]
    [InlineData("$", "$/")]
    [InlineData("$/Product/", "$/Product")]
    public void NormalizeServerPath_TrimsTrailingSlash(string input, string expected)
    {
        Assert.Equal(expected, TfvcWorkspace.NormalizeServerPath(input));
    }

    [Fact]
    public void CombineServerPath_HandlesRoot()
    {
        Assert.Equal("$/Product", TfvcWorkspace.CombineServerPath("$/", "Product"));
        Assert.Equal("$/Product/src", TfvcWorkspace.CombineServerPath("$/Product", "src"));
    }

    [Fact]
    public void SelectPreferredWorkspace_PrefersWorkspaceOfEditorPath()
    {
        TfvcWorkspace first = TfvcTestData.CreateWorkspace("FIRST", @"C:\first");
        TfvcWorkspace second = TfvcTestData.CreateWorkspace("SECOND", @"C:\second");
        List<TfvcWorkspace> workspaces = [first, second];

        Assert.Same(second, TfvcSession.SelectPreferredWorkspace(workspaces, @"C:\second\app\app.csproj", "FIRST"));
        Assert.Same(first, TfvcSession.SelectPreferredWorkspace(workspaces, @"C:\unmapped", "first"));
        Assert.Null(TfvcSession.SelectPreferredWorkspace([], null, null));
    }

    [Fact]
    public void FormatChangeTypes_UsesKnownNamesAndKeepsUnknownOnes()
    {
        string text = TfvcDisplayText.FormatChangeTypes(["Edit", "Custom"]);

        Assert.EndsWith(", Custom", text);
        Assert.DoesNotContain("Edit", text);
    }

    [Fact]
    public void TfExeLocator_UsesConfiguredPathOrNewestDetectedInstallation()
    {
        HashSet<string> files = new(System.StringComparer.OrdinalIgnoreCase)
        {
            @"C:\VS\2022\Community\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\TF.exe",
            @"C:\custom\tf.exe"
        };
        TfExeLocator locator = new(files.Contains, () => [@"C:\VS\18\Community", @"C:\VS\2022\Community"]);

        Assert.Equal(@"C:\custom\tf.exe", locator.Locate(@"""C:\custom\tf.exe"""));
        Assert.Null(locator.Locate(@"C:\missing\tf.exe"));
        Assert.Equal(
            @"C:\VS\2022\Community\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\TF.exe",
            locator.Locate(null));
    }
}
