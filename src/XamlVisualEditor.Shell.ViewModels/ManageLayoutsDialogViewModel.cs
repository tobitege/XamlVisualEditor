using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Renames and deletes the saved window layouts.</summary>
public sealed class ManageLayoutsDialogViewModel : ReactiveObject, IDisposable
{
    private readonly IUserLayoutStore _store;
    private readonly Action<string, string> _renamed;
    private readonly Action<string> _deleted;
    private string? _selectedName;
    private string _editName = string.Empty;
    private string? _errorText;

    /// <param name="store">The store of the saved layouts.</param>
    /// <param name="renamed">Receives the old and the new name after a layout was renamed.</param>
    /// <param name="deleted">Receives the name of a deleted layout.</param>
    public ManageLayoutsDialogViewModel(IUserLayoutStore store, Action<string, string> renamed, Action<string> deleted)
    {
        _store = store;
        _renamed = renamed;
        _deleted = deleted;
        Names = new ObservableCollection<string>(store.ListNames());
        Names.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasNames));
        RenameCommand = ReactiveCommand.Create(
            Rename,
            this.WhenAnyValue(x => x.SelectedName, x => x.EditName, (selected, edit) =>
                selected is not null && edit.Trim().Length > 0 && !string.Equals(selected, edit.Trim(), StringComparison.Ordinal)));
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync, this.WhenAnyValue(x => x.SelectedName).Select(name => name is not null));
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke());
        SelectedName = Names.FirstOrDefault();
    }

    public ObservableCollection<string> Names { get; }

    public bool HasNames => Names.Count > 0;

    public ReactiveCommand<RxVoid, RxVoid> RenameCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> DeleteCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CloseCommand { get; }

    /// <summary>Asks the user to confirm deleting the named layout; the output is true when confirmed.</summary>
    public Interaction<string, bool> ConfirmDeleteInteraction { get; } = new();

    public event Action? CloseRequested;

    public string? SelectedName
    {
        get => _selectedName;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedName, value);
            EditName = value ?? string.Empty;
        }
    }

    /// <summary>The new name for the selected layout.</summary>
    public string EditName
    {
        get => _editName;
        set
        {
            this.RaiseAndSetIfChanged(ref _editName, value);
            ErrorText = null;
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => this.RaiseAndSetIfChanged(ref _errorText, value);
    }

    private void Rename()
    {
        string oldName = SelectedName!;
        string newName = EditName.Trim();
        if (!_store.IsValidName(newName))
        {
            ErrorText = UiStrings.LayoutInvalidName;
            return;
        }

        if (!_store.Rename(oldName, newName))
        {
            ErrorText = _store.Exists(oldName)
                ? string.Format(UiStrings.LayoutNameExists, newName)
                : string.Format(UiStrings.LayoutNotFound, oldName);
            return;
        }

        Reload(newName);
        _renamed(oldName, newName);
    }

    private async Task DeleteAsync()
    {
        string name = SelectedName!;
        if (!await ConfirmDeleteInteraction.Handle(name))
        {
            return;
        }

        int index = Names.IndexOf(name);
        bool deleted = _store.Delete(name);
        Reload(null, index);
        if (deleted)
        {
            _deleted(name);
        }
        else
        {
            ErrorText = string.Format(UiStrings.LayoutNotFound, name);
        }
    }

    // Reads the names again, because the store sorts them; without a name to select, the entry at the index
    // of the removed one gets the selection.
    private void Reload(string? select, int index = 0)
    {
        Names.Clear();
        foreach (string name in _store.ListNames())
        {
            Names.Add(name);
        }

        SelectedName = select is not null && Names.Contains(select)
            ? select
            : Names.Count > 0 ? Names[Math.Clamp(index, 0, Names.Count - 1)] : null;
    }

    public void Dispose()
    {
        RenameCommand.Dispose();
        DeleteCommand.Dispose();
        CloseCommand.Dispose();
    }
}
