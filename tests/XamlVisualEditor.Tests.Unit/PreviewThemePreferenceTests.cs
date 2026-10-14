using XamlVisualEditor.Core;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PreviewThemePreferenceTests
{
    private static readonly DesignerThemeSelection Derived = new("example.palette-themes/palette", "dark");
    private static readonly DesignerThemeSelection Chosen = new("example.palette-themes/palette", "corporate");

    [Fact]
    public void WithoutAStoredChoiceThePreviewFollowsTheEditorOptions()
    {
        Assert.Equal(Derived, PreviewThemePreference.GetEffective(null, Derived));
        Assert.Null(PreviewThemePreference.GetEffective(null, null));
        Assert.False(PreviewThemePreference.IsExplicitChoice(null));
    }

    [Fact]
    public void AStoredChoiceWinsOverTheEditorOptions()
    {
        Assert.Equal(Chosen, PreviewThemePreference.GetEffective(Chosen, Derived));
        Assert.True(PreviewThemePreference.IsExplicitChoice(Chosen));
    }

    [Fact]
    public void PickingTheProjectDefaultStoresAMarkerOnlyWhenTheOptionsSupplyATheme()
    {
        DesignerThemeSelection? stored = PreviewThemePreference.ToStored(null, Derived);

        Assert.Equal(PreviewThemePreference.ExplicitProjectDefault, stored);
        Assert.Null(PreviewThemePreference.GetEffective(stored, Derived));
        Assert.False(PreviewThemePreference.IsExplicitChoice(stored));
        Assert.Null(PreviewThemePreference.ToStored(null, null));
    }

    [Fact]
    public void PickingTheThemeTheOptionsSupplyKeepsFollowingTheOptions()
    {
        Assert.Null(PreviewThemePreference.ToStored(Derived, Derived));
        Assert.Equal(Chosen, PreviewThemePreference.ToStored(Chosen, Derived));
    }
}
