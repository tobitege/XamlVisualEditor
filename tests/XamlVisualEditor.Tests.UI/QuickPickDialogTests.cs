using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.Tests.UI;

public sealed class QuickPickDialogTests
{
    [AvaloniaFact]
    public async Task DoubleClickOnRowConfirmsThatItemAndWindowTitleFollowsViewModel()
    {
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Avalonia.Markup.Xaml.Styling.StyleInclude gridTheme = new(new Uri("avares://XamlVisualEditor.Tests.UI/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml")
        };
        Application.Current!.Styles.Add(theme);
        Application.Current.Styles.Add(gridTheme);
        QuickPickItem first = new("Open in current window", "Replace current workspace", null);
        QuickPickItem second = new("Open in new window", "Open folder in a new window", null);
        QuickPickDialogViewModel viewModel = new("Open Folder", new[] { first, second });
        Window owner = new();
        QuickPickDialog dialog = new() { DataContext = viewModel };
        try
        {
            owner.Show();
            Task<QuickPickItem?> result = dialog.ShowDialog<QuickPickItem?>(owner);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            dialog.UpdateLayout();

            Assert.Equal("Open Folder", dialog.Title);
            Assert.Same(viewModel.Items[0], viewModel.SelectedItem);

            DataGridRow row = dialog.GetVisualDescendants().OfType<DataGridRow>()
                .Single(candidate => ReferenceEquals(candidate.DataContext, viewModel.Items[1]));
            Point center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), dialog)!.Value;
            dialog.MouseDown(center, MouseButton.Left);
            dialog.MouseUp(center, MouseButton.Left);
            dialog.MouseDown(center, MouseButton.Left);
            // Der zweite Klick schließt den Dialog bereits beim Drücken.
            if (dialog.PlatformImpl is not null)
            {
                dialog.MouseUp(center, MouseButton.Left);
            }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            QuickPickItem? picked = await result.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(second, picked);
            Assert.Same(viewModel.Items[1], viewModel.SelectedItem);
            Assert.False(dialog.IsVisible);
        }
        finally
        {
            if (dialog.IsVisible)
            {
                dialog.Close();
            }

            owner.Close();
            Application.Current!.Styles.Remove(gridTheme);
            Application.Current.Styles.Remove(theme);
        }
    }
}
