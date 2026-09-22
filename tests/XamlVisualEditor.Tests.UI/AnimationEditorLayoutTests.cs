using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class AnimationEditorLayoutTests
{
    private const double Tolerance = 0.5;

    [AvaloniaTheory]
    [InlineData(560, 240, 3)]
    [InlineData(700, 240, 2)]
    [InlineData(900, 240, 1)]
    [InlineData(1400, 320, 1)]
    public async Task ToolbarWrapsInsteadOfOverflowingAndInspectorStaysInsideThePanel(double width, double height, int toolbarRows)
    {
        using MainWindowViewModel main = new();
        AnimationEditorViewModel editor = main.AnimationEditor;
        await editor.AddTimelineCommand.Execute();
        editor.NewTrackPropertyName = "Opacity";
        await editor.AddTrackCommand.Execute();
        editor.NewTrackPropertyName = "RenderTransform.ScaleX";
        await editor.AddTrackCommand.Execute();
        await editor.AddKeyframeCommand.Execute();

        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        AnimationEditorView view = new() { DataContext = editor };
        Window window = new() { Content = view, Width = width, Height = height };
        window.Show();
        try
        {
            await Idle();
            view.UpdateLayout();

            Border toolbar = view.FindControl<Border>("AnimationToolbar")!;
            Border inspector = view.FindControl<Border>("AnimationInspector")!;
            ScrollViewer timeline = view.FindControl<ScrollViewer>("TimelineScroll")!;

            // One toolbar row is a control height plus padding; a wrapped toolbar adds another control height.
            double rowHeight = 32 + 2;
            Assert.InRange(toolbar.Bounds.Height, 20, 8 + toolbarRows * rowHeight);
            Assert.Equal(280, inspector.Bounds.Width, 1.0);
            Assert.True(timeline.Bounds.Height > height * 0.5, $"timeline height {timeline.Bounds.Height} of {height}");
            Assert.True(timeline.Bounds.Width >= width - inspector.Bounds.Width - 1, $"timeline width {timeline.Bounds.Width} of {width}");

            List<string> failures = new();
            foreach (Control control in OwnControls(toolbar))
            {
                Rect rect = ToViewRect(control, view);
                if (rect.Left < -Tolerance || rect.Right > view.Bounds.Width + Tolerance
                    || rect.Top < -Tolerance || rect.Bottom > toolbar.Bounds.Height + Tolerance)
                {
                    failures.Add($"{Describe(control)} leaves the toolbar: {rect} in {view.Bounds.Size}");
                }
            }

            // The inspector scrolls vertically, so only its horizontal extent must fit.
            foreach (Control control in OwnControls(inspector))
            {
                Rect rect = ToViewRect(control, view);
                if (rect.Left < inspector.Bounds.Left - Tolerance || rect.Right > view.Bounds.Width + Tolerance)
                {
                    failures.Add($"{Describe(control)} leaves the inspector: {rect} in {view.Bounds.Size}");
                }
            }

            foreach (Panel panel in OwnControls(toolbar).Concat(OwnControls(inspector)).OfType<Panel>())
            {
                Control[] children = panel.Children
                    .Where(child => child.IsEffectivelyVisible && child.Bounds.Width > 0 && child.Bounds.Height > 0)
                    .ToArray();
                for (int i = 0; i < children.Length; i++)
                {
                    for (int j = i + 1; j < children.Length; j++)
                    {
                        Rect intersection = ToViewRect(children[i], view).Intersect(ToViewRect(children[j], view));
                        if (intersection.Width > Tolerance && intersection.Height > Tolerance)
                        {
                            failures.Add($"{Describe(children[i])} overlaps {Describe(children[j])} inside {Describe(panel)}");
                        }
                    }
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

            // Text-only buttons are gone from the toolbar; every action is an icon button with a tooltip.
            Button[] toolbarButtons = toolbar.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Equal(10, toolbarButtons.Length);
            Assert.All(toolbarButtons, button =>
            {
                Assert.IsType<PathIcon>(button.Content);
                Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(button) as string));
            });

            // Icon buttons drive the same commands as before.
            Assert.Contains(toolbarButtons, button => ReferenceEquals(button.Command, editor.PlayPreviewCommand));
            Assert.Contains(toolbarButtons, button => ReferenceEquals(button.Command, editor.CopyKeyframesCommand));
            Assert.Contains(toolbarButtons, button => ReferenceEquals(button.Command, editor.PasteKeyframesCommand));
            Assert.Contains(toolbarButtons, button => ReferenceEquals(button.Command, editor.DeleteSelectionCommand));
            Button[] inspectorButtons = inspector.GetVisualDescendants().OfType<Button>().ToArray();
            Assert.Contains(inspectorButtons, button => ReferenceEquals(button.Command, editor.CreateResourceCommand));
            Assert.Contains(inspectorButtons, button => ReferenceEquals(button.Command, editor.DeleteResourceCommand));

            // Keyframe details live in the inspector and follow the selection.
            ContentControl keyframeEditor = view.FindControl<ContentControl>("KeyframeEditor")!;
            Assert.Same(editor.SelectedKeyframe, keyframeEditor.Content);
            Assert.NotNull(editor.SelectedKeyframe);
            Assert.NotEmpty(keyframeEditor.GetVisualDescendants().OfType<TextBox>());
            TextBlock validation = view.FindControl<TextBlock>("KeyframeValidation")!;
            Assert.Equal(!string.IsNullOrEmpty(editor.KeyframeValidationMessage), validation.IsVisible);
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
        }
    }

    [AvaloniaFact]
    public async Task SelectedTrackHeaderIsHighlightedAndFollowsTheSelection()
    {
        using MainWindowViewModel main = new();
        AnimationEditorViewModel editor = main.AnimationEditor;
        await editor.AddTimelineCommand.Execute();
        editor.NewTrackPropertyName = "Opacity";
        await editor.AddTrackCommand.Execute();
        editor.NewTrackPropertyName = "Width";
        await editor.AddTrackCommand.Execute();
        AnimationTrackViewModel first = editor.SelectedTimeline!.Tracks[0];
        AnimationTrackViewModel second = editor.SelectedTimeline.Tracks[1];
        Assert.False(first.IsSelected);
        Assert.True(second.IsSelected);

        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        AnimationEditorView view = new() { DataContext = editor };
        Window window = new() { Content = view, Width = 1000, Height = 300 };
        window.Show();
        try
        {
            await Idle();
            view.UpdateLayout();
            ItemsControl trackList = view.FindControl<ItemsControl>("TrackList")!;
            Assert.Equal(2, trackList.ItemCount);
            Border[] headers = trackList.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("track-header"))
                .ToArray();
            Assert.Equal(2, headers.Length);
            Assert.DoesNotContain("selected", headers[0].Classes);
            Assert.Contains("selected", headers[1].Classes);

            Button selectFirst = headers[0].GetVisualDescendants().OfType<Button>().Single();
            Assert.Same(editor.SelectTrackCommand, selectFirst.Command);
            Assert.Same(first, selectFirst.CommandParameter);
            await editor.SelectTrackCommand.Execute(first);
            await Idle();

            Assert.True(first.IsSelected);
            Assert.False(second.IsSelected);
            Assert.Contains("selected", headers[0].Classes);
            Assert.DoesNotContain("selected", headers[1].Classes);
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
        }
    }

    private static IEnumerable<Control> OwnControls(Control root)
    {
        return root.GetVisualDescendants()
            .OfType<Control>()
            .Where(control => control.TemplatedParent is null && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0);
    }

    private static Task Idle() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();

    private static Rect ToViewRect(Control control, Control view)
    {
        Point origin = control.TranslatePoint(default, view) ?? throw new Xunit.Sdk.XunitException($"{Describe(control)} is not attached to the view.");
        return new Rect(origin, control.Bounds.Size);
    }

    private static string Describe(Control control)
    {
        return string.IsNullOrEmpty(control.Name)
            ? $"{control.GetType().Name}"
            : $"{control.GetType().Name}#{control.Name}";
    }
}
