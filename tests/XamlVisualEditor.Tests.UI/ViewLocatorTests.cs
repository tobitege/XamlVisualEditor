using Avalonia.Headless.XUnit;
using XamlVisualEditor.App;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class ViewLocatorTests
{
    [AvaloniaFact]
    public void ReferencesViewModelIsMatchedAndBuildsTheReferencesView()
    {
        using ReferencesViewModel references = new(_ => Task.CompletedTask);
        XamlVisualEditor.App.ViewLocator locator = new();

        Assert.True(locator.Match(references));
        Assert.IsType<ReferencesView>(locator.Build(references));
    }
}
