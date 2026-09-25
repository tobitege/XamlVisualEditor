using XamlVisualEditor.Designer.PreviewerHost;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignTreeReporterTests
{
    [Fact]
    public void Read_PrefersTheMostDerivedDeclarationOfAHiddenProperty()
    {
        HidingControl control = new() { Padding = "derived" };
        ((BaseControl)control).Padding = 4;

        Assert.Equal("derived", DesignTreeReporter.Read(control, "Padding"));
    }

    [Fact]
    public void Read_SelectsTheHiddenDeclarationByPropertyType()
    {
        HidingControl control = new() { Padding = "derived" };
        ((BaseControl)control).Padding = 4;

        Assert.Equal(4d, DesignTreeReporter.Read(control, "Padding", typeof(double).FullName));
    }

    [Fact]
    public void Read_ReturnsNullForAMissingPropertyOrType()
    {
        HidingControl control = new();

        Assert.Null(DesignTreeReporter.Read(control, "Missing"));
        Assert.Null(DesignTreeReporter.Read(control, "Padding", "Avalonia.Thickness"));
    }

    private class BaseControl
    {
        public double Padding { get; set; }
    }

    private sealed class HidingControl : BaseControl
    {
        public new string? Padding { get; set; }
    }
}
