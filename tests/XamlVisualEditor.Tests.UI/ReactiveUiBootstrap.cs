using System;
using System.IO;
using System.Runtime.CompilerServices;
using ReactiveUI;
using ReactiveUI.Builder;
using XamlVisualEditor.Shell;

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
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithPlatformServices()
            .WithCoreServices()
            .BuildApp();

        // Tests build real shell view models; without an isolated settings root
        // they read and overwrite the user's persisted layout files.
        XamlEditorDockFactory.SettingsRootOverride = Path.Combine(
            Path.GetTempPath(), "xve-tests-layout", Guid.NewGuid().ToString("N"));
    }
}
