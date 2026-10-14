using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(XamlVisualEditor.Tests.UI.HeadlessTestAppBuilder))]

namespace XamlVisualEditor.Tests.UI;

public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        base.Initialize();
        ReactiveUI.RxSchedulers.MainThreadScheduler =
            ReactiveUI.Primitives.Concurrency.AvaloniaScheduler.For(Avalonia.Threading.Dispatcher.UIThread);
    }
}

public static class HeadlessTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
