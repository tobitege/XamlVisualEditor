using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using XamlVisualEditor.App.Styles;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class CompactTabStripPanelTests
{
    [Fact]
    public void Width_cap_is_unbounded_while_all_tabs_fit()
    {
        Assert.Equal(double.PositiveInfinity, CompactTabStripPanel.ComputeWidthCap([140, 120, 80], 400));
        Assert.Equal(double.PositiveInfinity, CompactTabStripPanel.ComputeWidthCap([140, 120], double.PositiveInfinity));
    }

    [Fact]
    public void Width_cap_shrinks_the_widest_tabs_first()
    {
        // 80 keeps its width; the remaining 220 are shared by the two wider tabs.
        Assert.Equal(110, CompactTabStripPanel.ComputeWidthCap([140, 130, 80], 300));
        Assert.Equal(CompactTabStripPanel.MinimumTabWidth, CompactTabStripPanel.ComputeWidthCap([140, 130, 80], 60));
    }

    [AvaloniaFact]
    public void Tabs_that_do_not_fit_shrink_their_titles_to_the_available_width()
    {
        CompactTabStripPanel panel = new();
        foreach (string title in new[] { "Solution Explorer", "File Explorer", "Toolbox", "TFVC Explorer" })
        {
            panel.Children.Add(new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Children = { new Border { Width = 12 }, new TextBlock { Text = title } }
            });
        }

        panel.Measure(new Size(double.PositiveInfinity, 30));
        double naturalWidth = panel.DesiredSize.Width;
        panel.Measure(new Size(naturalWidth * 0.6, 30));

        // Trimming stops at whole characters, so each shortened title may end up to one glyph narrower.
        Assert.InRange(panel.DesiredSize.Width, naturalWidth * 0.6 - 4 * 16, naturalWidth * 0.6);

        // A repeated measure with the same width leaves the shortened titles alone (no layout cycle).
        TextBlock[] titles = panel.Children.Select(tab => ((StackPanel)tab).Children.OfType<TextBlock>().Single()).ToArray();
        double[] limits = titles.Select(title => title.MaxWidth).ToArray();
        panel.InvalidateMeasure();
        panel.Measure(new Size(naturalWidth * 0.6, 30));
        Assert.Equal(limits, titles.Select(title => title.MaxWidth).ToArray());
        Assert.All(titles, title => Assert.True(title.IsMeasureValid));

        panel.Measure(new Size(double.PositiveInfinity, 30));
        Assert.Equal(naturalWidth, panel.DesiredSize.Width, 3);
    }
}
