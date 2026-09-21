using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(XamlVisualEditor.Tests.UI.HeadlessTestAppBuilder))]

namespace XamlVisualEditor.Tests.UI;

public sealed class HeadlessTestApp : Application
{
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
