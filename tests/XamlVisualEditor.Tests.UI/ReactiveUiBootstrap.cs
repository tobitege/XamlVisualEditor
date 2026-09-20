using System.Runtime.CompilerServices;
using ReactiveUI;
using ReactiveUI.Avalonia;
using ReactiveUI.Builder;

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

        // UI-bound command notifications must return to the Avalonia dispatcher.
        RxSchedulers.MainThreadScheduler = AvaloniaScheduler.Instance;
    }
}
