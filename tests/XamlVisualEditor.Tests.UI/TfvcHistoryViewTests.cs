using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;
using XamlVisualEditor.TfvcExtension.ViewModels;
using XamlVisualEditor.TfvcExtension.Views;

namespace XamlVisualEditor.Tests.UI;

public sealed class TfvcHistoryViewTests
{
    [AvaloniaFact]
    public void History_grid_shows_newest_changeset_first_with_descending_sort_indicator()
    {
        using TfvcSession session = CreateSession();
        TfvcHistoryViewModel viewModel = new(session);
        foreach (int id in new[] { 1, 3, 2 })
        {
            viewModel.Changesets.Add(new TfvcChangesetRowViewModel(new TfvcChangesetInfo(id, "Test User", null, "Comment " + id)));
        }

        ShowThemed(new TfvcHistoryView { DataContext = viewModel }, window =>
        {
            DataGrid grid = window.GetVisualDescendants().OfType<DataGrid>().First(candidate => ReferenceEquals(candidate.SortingModel, viewModel.SortingModel));
            DataGridColumn changesetColumn = grid.Columns.First(column => column.SortMemberPath == nameof(TfvcChangesetRowViewModel.Id));
            Assert.Equal(ListSortDirection.Descending, changesetColumn.SortDirection);

            int[] displayedIds = grid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Where(row => row.IsVisible && row.DataContext is TfvcChangesetRowViewModel)
                .OrderBy(row => row.Bounds.Top)
                .Select(row => ((TfvcChangesetRowViewModel)row.DataContext!).Id)
                .ToArray();
            Assert.Equal([3, 2, 1], displayedIds);
        });
    }

    [AvaloniaFact]
    public async Task Changeset_details_show_comment_read_only_for_other_authors()
    {
        using TfvcSession session = CreateSession();
        TfvcChangesetDetailsViewModel viewModel = new(session);
        await viewModel.ShowAsync(new TfvcChangesetInfo(42, "Other User", null, "Original comment", "other-id"), CancellationToken.None);

        ShowThemed(new TfvcChangesetDetailsView { DataContext = viewModel }, window =>
        {
            TextBox commentBox = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Text == "Original comment");
            Assert.True(commentBox.IsReadOnly);
            Assert.DoesNotContain(
                window.GetVisualDescendants().OfType<Button>(),
                button => button.IsEffectivelyVisible && ReferenceEquals(button.Command, viewModel.SaveCommand));
        });
    }

    // Mirrors the Fluent and DataGrid themes of App.axaml; the headless test application has no styles of its own.
    private static void ShowThemed(Control content, Action<Window> assert)
    {
        Uri baseUri = new("avares://XamlVisualEditor.App/");
        IStyle[] styles =
        [
            new FluentTheme(),
            new StyleInclude(baseUri) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml") }
        ];
        Application.Current!.Styles.AddRange(styles);
        Window window = new() { Content = content, Width = 900, Height = 500 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            assert(window);
        }
        finally
        {
            window.Close();
            foreach (IStyle style in styles)
            {
                Application.Current!.Styles.Remove(style);
            }
        }
    }

    // The views only read local state here; the TFVC clients are never called without a collection URL.
    private static TfvcSession CreateSession()
    {
        return new TfvcSession(null!, null!, new TfvcSettingsStore(new MemoryStorage()), new EmptyWorkspaceInfo(), new SilentLogger());
    }

    private sealed class MemoryStorage : IExtensionStorage
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
            => Task.FromResult(_values.TryGetValue(key, out object? value) ? (T?)value : default);

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyWorkspaceInfo : IWorkspaceInfo
    {
        public string? WorkspacePath => null;

        public event EventHandler<WorkspaceChangedEventArgs> WorkspaceChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class SilentLogger : IExtensionLogger
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? exception = null)
        {
        }
    }
}
