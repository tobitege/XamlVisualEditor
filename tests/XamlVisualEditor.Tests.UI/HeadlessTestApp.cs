using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(XamlVisualEditor.Tests.UI.HeadlessTestAppBuilder))]
// All tests share one headless UI thread and Application.Current.Styles; interleaving test classes
// lets one test's theme add/remove re-template another test's open window.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

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
        XamlVisualEditor.Core.Localization.UiStrings.Culture = System.Globalization.CultureInfo.GetCultureInfo("en");
        return AppBuilder.Configure<HeadlessTestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
