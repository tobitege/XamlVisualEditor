using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class EditorDensityTabItemTests
{
    [AvaloniaFact]
    public void CompactTabItemDrawsTheSelectionPipeBelowItsHeaderText()
    {
        IStyle[] styles = [];
        Window? window = null;
        try
        {
            Uri baseUri = new("avares://XamlVisualEditor.App/");
            styles =
            [
                new Avalonia.Themes.Fluent.FluentTheme(),
                new Avalonia.Markup.Xaml.Styling.StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorDensity.axaml") },
            ];
            Application.Current!.Styles.AddRange(styles);
            TabItem selected = new() { Header = "Unstaged (2)", Content = new TextBlock { Text = "content" } };
            TabControl tabs = new() { Items = { selected, new TabItem { Header = "Staged (0)" } }, SelectedIndex = 0 };
            window = new Window { Content = tabs, Width = 400, Height = 200, Classes = { "editor-chrome" } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Control pipe = selected.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_SelectedPipe");
            ContentPresenter header = selected.GetVisualDescendants().OfType<ContentPresenter>().Single(presenter => presenter.Name == "PART_ContentPresenter");
            Assert.True(pipe.IsEffectivelyVisible);
            double headerBottom = header.TranslatePoint(new Point(0, header.Bounds.Height), selected)!.Value.Y;
            Point pipeTop = pipe.TranslatePoint(new Point(0, 0), selected)!.Value;
            double pipeBottom = pipe.TranslatePoint(new Point(0, pipe.Bounds.Height), selected)!.Value.Y;

            Assert.True(pipeTop.Y >= headerBottom, $"The pipe starts at {pipeTop.Y} inside the header text ending at {headerBottom}.");
            Assert.True(pipeBottom <= selected.Bounds.Height, $"The pipe ends at {pipeBottom} below the tab of height {selected.Bounds.Height}.");
        }
        finally
        {
            window?.Close();
            foreach (IStyle style in styles)
                Application.Current!.Styles.Remove(style);
        }
    }
}
