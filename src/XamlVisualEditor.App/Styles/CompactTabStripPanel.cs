using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace XamlVisualEditor.App.Styles;

/// <summary>
/// Lays tool tabs out in one row that never needs scrolling: tabs keep their natural width while they fit, otherwise
/// the widest tabs shrink first and shorten their titles with an ellipsis.
/// </summary>
/// <remarks>
/// The Dock tab strip hosts its items panel in a horizontally scrolling viewer, so the panel measures against the
/// width of that viewer instead of the unbounded width it receives. The tab chrome lays its title out in a horizontal
/// stack with unbounded width, so the panel limits the width of the title text block of each tab itself. The natural
/// title width is remembered per title, because a shortened title no longer reports it.
/// </remarks>
public sealed class CompactTabStripPanel : Panel
{
    /// <summary>Smallest width a shrunk tab keeps.</summary>
    public const double MinimumTabWidth = 48;

    // Changes below this width are rounding noise; applying them would re-measure the tabs without visible effect.
    private const double WidthTolerance = 0.5;

    private static readonly AttachedProperty<double> NaturalTitleWidthProperty =
        AvaloniaProperty.RegisterAttached<CompactTabStripPanel, TextBlock, double>("NaturalTitleWidth");

    private static readonly AttachedProperty<string?> NaturalTitleTextProperty =
        AvaloniaProperty.RegisterAttached<CompactTabStripPanel, TextBlock, string?>("NaturalTitleText");

    private ScrollViewer? _viewer;
    private IDisposable? _viewerBounds;
    private double _viewerWidth;

    /// <summary>
    /// Returns the widest tab width at which tabs of the given natural widths fit into the available width, or
    /// <see cref="double.PositiveInfinity"/> when all tabs fit at their natural width.
    /// </summary>
    public static double ComputeWidthCap(double[] naturalWidths, double availableWidth)
    {
        if (!double.IsFinite(availableWidth) || naturalWidths.Sum() <= availableWidth)
        {
            return double.PositiveInfinity;
        }

        double[] sorted = naturalWidths.OrderBy(width => width).ToArray();
        double remaining = availableWidth;
        for (int index = 0; index < sorted.Length; index++)
        {
            double share = remaining / (sorted.Length - index);
            if (sorted[index] > share)
            {
                return Math.Max(MinimumTabWidth, share);
            }

            remaining -= sorted[index];
        }

        return double.PositiveInfinity;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _viewerBounds?.Dispose();
        _viewerBounds = null;
        _viewer = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size childConstraint = new(double.PositiveInfinity, availableSize.Height);
        double[] naturalWidths = new double[Children.Count];
        TextBlock?[] titles = new TextBlock?[Children.Count];
        for (int index = 0; index < Children.Count; index++)
        {
            Control child = Children[index];
            child.Measure(childConstraint);
            titles[index] = FindTitle(child);
            naturalWidths[index] = titles[index] is { } title
                ? child.DesiredSize.Width - title.DesiredSize.Width + GetNaturalTitleWidth(title)
                : child.DesiredSize.Width;
        }

        double availableWidth = double.IsFinite(availableSize.Width) ? availableSize.Width : GetViewerWidth();
        double cap = ComputeWidthCap(naturalWidths, availableWidth);

        double width = 0;
        double height = 0;
        for (int index = 0; index < Children.Count; index++)
        {
            Control child = Children[index];
            if (titles[index] is { } title)
            {
                double reduction = double.IsInfinity(cap) ? 0 : Math.Max(0, naturalWidths[index] - cap);
                if (ApplyTitleReduction(title, reduction))
                {
                    // The containers between tab and title still hold a valid measure for the unchanged
                    // constraint; without invalidating them the tab would keep its old width and the layout
                    // manager would re-measure the title later, which starts a layout cycle.
                    InvalidateMeasureUpTo(title, child);
                    child.Measure(childConstraint);
                }
            }

            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (Control child in Children)
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }

        return finalSize;
    }

    private static void InvalidateMeasureUpTo(Visual descendant, Control ancestor)
    {
        for (Visual? current = descendant; current is not null; current = current.GetVisualParent())
        {
            if (current is Layoutable layoutable)
            {
                layoutable.InvalidateMeasure();
            }

            if (ReferenceEquals(current, ancestor))
            {
                break;
            }
        }
    }

    // The title is the text block with the longest text (icon and state markers are shorter or absent).
    private static TextBlock? FindTitle(Control tab) =>
        tab.GetVisualDescendants().OfType<TextBlock>()
            .OrderByDescending(block => block.Text?.Length ?? 0)
            .FirstOrDefault();

    private static double GetNaturalTitleWidth(TextBlock title)
    {
        if (double.IsPositiveInfinity(title.MaxWidth))
        {
            return title.DesiredSize.Width;
        }

        if (!string.Equals(title.GetValue(NaturalTitleTextProperty), title.Text, StringComparison.Ordinal))
        {
            // The text changed while it was shortened: measure it once without the limit.
            title.ClearValue(MaxWidthProperty);
            title.Measure(Size.Infinity);
            RememberNaturalWidth(title);
        }

        return title.GetValue(NaturalTitleWidthProperty);
    }

    private static void RememberNaturalWidth(TextBlock title)
    {
        title.SetValue(NaturalTitleWidthProperty, title.DesiredSize.Width);
        title.SetValue(NaturalTitleTextProperty, title.Text);
    }

    // Limits the title to its natural width minus the reduction; returns whether its width limit changed.
    private static bool ApplyTitleReduction(TextBlock title, double reduction)
    {
        if (reduction <= WidthTolerance)
        {
            if (double.IsPositiveInfinity(title.MaxWidth))
            {
                return false;
            }

            title.ClearValue(MaxWidthProperty);
            return true;
        }

        if (double.IsPositiveInfinity(title.MaxWidth))
        {
            RememberNaturalWidth(title);
        }

        double maxWidth = Math.Max(0, title.GetValue(NaturalTitleWidthProperty) - reduction);
        if (Math.Abs(title.MaxWidth - maxWidth) <= WidthTolerance)
        {
            return false;
        }

        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.MaxWidth = maxWidth;
        return true;
    }

    private double GetViewerWidth()
    {
        if (_viewer is null)
        {
            _viewer = this.FindAncestorOfType<ScrollViewer>();
            _viewerBounds = _viewer?.GetObservable(BoundsProperty).Subscribe(OnViewerBoundsChanged);
        }

        _viewerWidth = _viewer?.Bounds.Width ?? 0;
        return _viewerWidth > 0 ? _viewerWidth : double.PositiveInfinity;
    }

    // Only a width change of the viewer changes the layout; its height and position follow the tab heights.
    private void OnViewerBoundsChanged(Rect bounds)
    {
        if (Math.Abs(bounds.Width - _viewerWidth) > WidthTolerance)
        {
            InvalidateMeasure();
        }
    }
}
