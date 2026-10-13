using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.TfvcExtension.ViewModels;

/// <summary>
/// Browses the unversioned items below a mapped folder and returns the ones to add to version control.
/// </summary>
public sealed partial class TfvcAddItemsDialogViewModel : ReactiveObject
{
    /// <summary>Dialog id registered with the dialog host.</summary>
    public const string DIALOG_ID = "tfvc.addItemsToFolder";

    private readonly TfvcAddItemsScanner _scanner;
    private readonly Func<string, bool, object?> _getIcon;
    private readonly Dictionary<string, TfvcAddEntry> _selected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the dialog model for a scanned folder.</summary>
    public TfvcAddItemsDialogViewModel(TfvcAddItemsScanner scanner, Func<string, bool, object?> getIcon)
    {
        _scanner = scanner;
        _getIcon = getIcon;
        CurrentFolder = scanner.RootFolder;

        UpCommand = ReactiveCommand.CreateFromTask(
            ct => ShowFolderAsync(Path.GetDirectoryName(CurrentFolder) ?? CurrentFolder, ct),
            this.WhenAnyValue(x => x.CanGoUp));
        OpenSelectedCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            if (SelectedItem is { Entry.IsFolder: true } folder)
            {
                await ShowFolderAsync(folder.Entry.LocalPath, ct);
            }
            else if (SelectedItem is { } file)
            {
                file.IsSelected = !file.IsSelected;
            }
        });
        FinishCommand = ReactiveCommand.CreateFromTask(
            async () => { await CloseInteraction.Handle([.. _selected.Values]); },
            this.WhenAnyValue(x => x.SelectedCount).Select(count => count > 0));
        CancelCommand = ReactiveCommand.CreateFromTask(async () => { await CloseInteraction.Handle(null); });
    }

    /// <summary>Gets the displayed local folder.</summary>
    [Reactive]
    public partial string CurrentFolder { get; private set; }

    /// <summary>Gets the server folder the displayed folder maps to.</summary>
    [Reactive]
    public partial string? TargetServerFolder { get; private set; }

    /// <summary>Gets whether a parent folder inside the scanned folder exists.</summary>
    [Reactive]
    public partial bool CanGoUp { get; private set; }

    /// <summary>Gets whether the displayed folder holds no item to add.</summary>
    [Reactive]
    public partial bool IsEmpty { get; private set; }

    /// <summary>Gets the number of selected items in all folders.</summary>
    [Reactive]
    public partial int SelectedCount { get; private set; }

    /// <summary>Gets or sets the highlighted row.</summary>
    [Reactive]
    public partial TfvcAddItemViewModel? SelectedItem { get; set; }

    /// <summary>Gets the items of the displayed folder.</summary>
    public ObservableCollection<TfvcAddItemViewModel> Items { get; } = new();

    /// <summary>Closes the dialog with the selected items, or null when it is cancelled.</summary>
    public Interaction<IReadOnlyList<TfvcAddEntry>?, RxVoid> CloseInteraction { get; } = new();

    /// <summary>Shows the parent folder.</summary>
    public ReactiveCommand<RxVoid, RxVoid> UpCommand { get; }

    /// <summary>Opens the highlighted folder or toggles the highlighted file.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenSelectedCommand { get; }

    /// <summary>Adds the selected items.</summary>
    public ReactiveCommand<RxVoid, RxVoid> FinishCommand { get; }

    /// <summary>Closes the dialog without adding.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    /// <summary>Shows a folder inside the scanned folder.</summary>
    public async Task ShowFolderAsync(string folder, CancellationToken cancellationToken)
    {
        IReadOnlyList<TfvcAddEntry> entries = await _scanner.ListAsync(folder, cancellationToken);
        CurrentFolder = Path.TrimEndingDirectorySeparator(folder);
        TargetServerFolder = _scanner.GetServerPath(CurrentFolder);
        CanGoUp = !string.Equals(CurrentFolder, _scanner.RootFolder, StringComparison.OrdinalIgnoreCase);
        Items.Clear();
        List<TfvcAddEntry> sorted = [.. entries];
        sorted.Sort((left, right) => left.IsFolder != right.IsFolder
            ? left.IsFolder ? -1 : 1
            : StringComparer.OrdinalIgnoreCase.Compare(left.LocalPath, right.LocalPath));
        foreach (TfvcAddEntry entry in sorted)
        {
            TfvcAddItemViewModel item = new(entry, _getIcon(entry.LocalPath, entry.IsFolder), _selected.ContainsKey(entry.LocalPath));
            item.WhenAnyValue(x => x.IsSelected).Skip(1).Subscribe(selected => OnSelectionChanged(entry, selected));
            Items.Add(item);
        }

        IsEmpty = Items.Count == 0;
    }

    private void OnSelectionChanged(TfvcAddEntry entry, bool selected)
    {
        if (selected)
        {
            _selected[entry.LocalPath] = entry;
        }
        else
        {
            _selected.Remove(entry.LocalPath);
        }

        SelectedCount = _selected.Count;
    }
}

/// <summary>One item of the add items dialog.</summary>
public sealed partial class TfvcAddItemViewModel : ReactiveObject
{
    /// <summary>Creates the item.</summary>
    public TfvcAddItemViewModel(TfvcAddEntry entry, object? icon, bool isSelected)
    {
        Entry = entry;
        Icon = icon;
        IsSelected = isSelected;
    }

    /// <summary>Gets the entry.</summary>
    public TfvcAddEntry Entry { get; }

    /// <summary>Gets the system icon.</summary>
    public object? Icon { get; }

    /// <summary>Gets or sets whether the item is added.</summary>
    [Reactive]
    public partial bool IsSelected { get; set; }

    /// <summary>Gets the file or folder name.</summary>
    public string Name => Path.GetFileName(Entry.LocalPath);

    /// <summary>Gets the last write time for display.</summary>
    public string ModifiedText => Entry.Modified.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Gets the type for display.</summary>
    public string TypeText => Entry.IsFolder
        ? TfvcStrings.FolderType
        : Path.GetExtension(Entry.LocalPath) is { Length: > 1 } extension
            ? string.Format(CultureInfo.CurrentCulture, TfvcStrings.FileTypeFormat, extension[1..].ToUpperInvariant())
            : TfvcStrings.FileType;

    /// <summary>Gets the size for display; empty for a folder.</summary>
    public string SizeText => Entry.Size is { } size
        ? string.Format(CultureInfo.CurrentCulture, TfvcStrings.SizeKilobytesFormat, Math.Max(1, (size + 1023) / 1024))
        : string.Empty;
}
