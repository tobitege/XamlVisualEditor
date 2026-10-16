using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.TfvcExtension.ViewModels;

/// <summary>
/// Browses the unversioned items below a mapped folder and returns the ones to add to version control.
/// </summary>
/// <remarks>
/// Checking a folder checks every item to add below it, and clearing it clears them; a folder shows as checked while
/// all of them are checked. The result lists only items to add, each on its own, so a cleared item stays out.
/// </remarks>
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

        UpCommand = ReactiveCommand.Create(
            () => ShowFolder(Path.GetDirectoryName(CurrentFolder) ?? CurrentFolder),
            this.WhenAnyValue(x => x.CanGoUp));
        OpenSelectedCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedItem is { Entry.IsFolder: true } folder)
            {
                ShowFolder(folder.Entry.LocalPath);
            }
            else if (SelectedItem is { } file)
            {
                file.IsSelected = !file.IsSelected;
            }
        });
        FinishCommand = ReactiveCommand.CreateFromTask(
            async () => { await CloseInteraction.Handle(GetSelection()); },
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
    public void ShowFolder(string folder)
    {
        IReadOnlyList<TfvcAddEntry> entries = _scanner.List(folder);
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
            bool isSelected = entry.IsFolder ? IsFolderSelected(entry.LocalPath) : _selected.ContainsKey(entry.LocalPath);
            TfvcAddItemViewModel item = new(entry, _getIcon(entry.LocalPath, entry.IsFolder), isSelected);
            item.WhenAnyValue(x => x.IsSelected).Skip(1).Subscribe(selected => OnSelectionChanged(entry, selected));
            Items.Add(item);
        }

        IsEmpty = Items.Count == 0;
    }

    /// <summary>Gets the items to add, sorted by path.</summary>
    internal IReadOnlyList<TfvcAddEntry> GetSelection()
    {
        List<TfvcAddEntry> selected = [.. _selected.Values];
        selected.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.LocalPath, right.LocalPath));
        return selected;
    }

    private bool IsFolderSelected(string folder)
    {
        IReadOnlyList<TfvcAddEntry> below = _scanner.ListCandidatesBelow(folder);
        bool isCandidate = _scanner.IsCandidate(folder);
        if (below.Count == 0 && !isCandidate)
        {
            return false;
        }

        if (isCandidate && !_selected.ContainsKey(folder))
        {
            return false;
        }

        foreach (TfvcAddEntry entry in below)
        {
            if (!_selected.ContainsKey(entry.LocalPath))
            {
                return false;
            }
        }

        return true;
    }

    private void OnSelectionChanged(TfvcAddEntry entry, bool selected)
    {
        List<TfvcAddEntry> affected = new();
        if (!entry.IsFolder || _scanner.IsCandidate(entry.LocalPath))
        {
            affected.Add(entry);
        }

        if (entry.IsFolder)
        {
            affected.AddRange(_scanner.ListCandidatesBelow(entry.LocalPath));
        }

        foreach (TfvcAddEntry item in affected)
        {
            if (selected)
            {
                _selected[item.LocalPath] = item;
            }
            else
            {
                _selected.Remove(item.LocalPath);
            }
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
