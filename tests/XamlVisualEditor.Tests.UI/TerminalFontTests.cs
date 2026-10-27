using Avalonia.Media;
using SkiaSharp;
using XamlVisualEditor.Terminal.Avalonia.Controls;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class TerminalFontTests
{
    [Fact]
    public void FontResolutionSkipsFamiliesThatAreNotInstalled()
    {
        // Each platform has one of the monospaced families after the missing one.
        SKTypeface typeface = TerminalControl.ResolveTypeface(
            new FontFamily("XVE Missing Font, Courier New, Menlo, DejaVu Sans Mono"));

        Assert.True(typeface.IsFixedPitch, typeface.FamilyName);
    }
}
