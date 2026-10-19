using System.Runtime.CompilerServices;
using ReactiveUI.Builder;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Tests.Shared;

namespace XamlVisualEditor.Tests.UI;

/// <summary>
/// ReactiveUI 23 no longer initializes itself on first use; the headless test
/// application does not run UseReactiveUI, so the module initializer performs
/// the equivalent setup once per test assembly.
/// </summary>
internal static class ReactiveUiBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        TestTempDirectory.Initialize();
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithPlatformServices()
            .WithCoreServices()
            .BuildApp();

        // Tests build real shell view models; without an isolated settings folder they
        // read and overwrite the user's persisted layout files.
        ShellSettingsPaths.RootOverride = Path.Combine(TestTempDirectory.Root, "settings");
    }
}
