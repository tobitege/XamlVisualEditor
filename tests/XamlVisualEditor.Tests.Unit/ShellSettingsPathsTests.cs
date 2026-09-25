using System.IO;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ShellSettingsPathsTests
{
    [Fact]
    public void Test_host_keeps_shell_state_out_of_the_user_profile()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string root = ShellSettingsPaths.GetRoot();

        Assert.Equal(ShellSettingsPaths.RootOverride, root);
        Assert.True(Directory.Exists(root));
        Assert.False(root.StartsWith(appData, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(root, "dock-layout.json"), XamlEditorDockFactory.GetDefaultLayoutPath());
    }
}
