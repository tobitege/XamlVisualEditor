using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.DataGridFiltering;
using Avalonia.Controls.DataGridSearching;
using Avalonia.Controls.DataGridSorting;
using Avalonia.Data.Core;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Verwaltet die vollständige Dateihistorie, ohne die Dateien zu verändern.</summary>
public sealed class RecentFilesDialogViewModel : ReactiveObject, IDisposable
{
    private RecentFileEntry? _selectedEntry;
    private string _searchText = string.Empty;

    public RecentFilesDialogViewModel(ObservableCollection<RecentFileEntry> entries)
    {
        Entries = entries;
        Columns = new[]
        {
            CreateColumn(UiStrings.Name, nameof(RecentFileEntry.DisplayName), entry => entry.DisplayName, 1),
            CreateColumn(UiStrings.Path, nameof(RecentFileEntry.FilePath), entry => entry.FilePath, 3)
        };
        RemoveCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedEntry is not { } entry) return;
            int index = Entries.IndexOf(entry);
            SelectedEntry = null;
            Entries.Remove(entry);
            if (Entries.Count > 0)
            {
                SelectedEntry = Entries[Math.Min(Math.Max(index, 0), Entries.Count - 1)];
            }
        }, this.WhenAnyValue(x => x.SelectedEntry).Select(entry => entry is not null));
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
    }

    public ObservableCollection<RecentFileEntry> Entries { get; }
    public IReadOnlyList<DataGridColumnDefinition> Columns { get; }
    public SortingModel SortingModel { get; } = new();
    public FilteringModel FilteringModel { get; } = new();
    public SearchModel SearchModel { get; } = new();
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public event Action? CloseRequested;

    public RecentFileEntry? SelectedEntry
    {
        get => _selectedEntry;
        set => this.RaiseAndSetIfChanged(ref _selectedEntry, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            string query = value.Trim();
            if (query.Length == 0)
            {
                FilteringModel.Clear();
                SearchModel.Clear();
            }
            else
            {
                FilteringModel.SetOrUpdate(new FilteringDescriptor(nameof(RecentFileEntry.FilePath),
                    FilteringOperator.Custom, propertyPath: nameof(RecentFileEntry.FilePath),
                    predicate: item => item is RecentFileEntry entry && entry.FilePath.Contains(query, StringComparison.OrdinalIgnoreCase)));
                SearchModel.SetOrUpdate(new SearchDescriptor(query, matchMode: SearchMatchMode.Contains,
                    scope: SearchScope.VisibleColumns, comparison: StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private static DataGridColumnDefinition CreateColumn(string header, string name, Func<RecentFileEntry, string> getter, int width)
    {
        ClrPropertyInfo property = new(name, target => getter((RecentFileEntry)target), null, typeof(string));
        return DataGridColumnDefinitionBuilder.For<RecentFileEntry>().Text(header: header, property: property,
            getter: getter, configure: column =>
            {
                column.ColumnKey = name;
                column.IsReadOnly = true;
                column.Width = new DataGridLength(width, DataGridLengthUnitType.Star);
            });
    }

    public void Dispose()
    {
        RemoveCommand.Dispose();
        CloseCommand.Dispose();
    }
}
