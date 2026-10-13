using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;

namespace XamlVisualEditor.TfvcExtension.ViewModels;

/// <summary>Lists local changes made outside version control and returns the ones to pend.</summary>
public sealed partial class TfvcReconcileDialogViewModel : ReactiveObject
{
    /// <summary>Dialog id registered with the dialog host.</summary>
    public const string DIALOG_ID = "tfvc.reconcile";

    /// <summary>Creates the dialog model with every change selected.</summary>
    public TfvcReconcileDialogViewModel(IReadOnlyList<TfvcOfflineChange> changes)
    {
        foreach (TfvcOfflineChange change in changes)
        {
            Items.Add(new TfvcReconcileItemViewModel(change));
        }

        ApplyCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            List<TfvcOfflineChange> selected = new();
            foreach (TfvcReconcileItemViewModel item in Items)
            {
                if (item.IsSelected)
                {
                    selected.Add(item.Change);
                }
            }

            await CloseInteraction.Handle(selected);
        });
        CancelCommand = ReactiveCommand.CreateFromTask(async () => { await CloseInteraction.Handle(null); });
    }

    /// <summary>Gets the listed changes.</summary>
    public ObservableCollection<TfvcReconcileItemViewModel> Items { get; } = new();

    /// <summary>Closes the dialog with the selected changes, or null when it is cancelled.</summary>
    public Interaction<IReadOnlyList<TfvcOfflineChange>?, RxVoid> CloseInteraction { get; } = new();

    /// <summary>Pends the selected changes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ApplyCommand { get; }

    /// <summary>Closes the dialog without changes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }
}

/// <summary>One local change in the reconcile dialog.</summary>
public sealed partial class TfvcReconcileItemViewModel : ReactiveObject
{
    /// <summary>Creates the item, selected.</summary>
    public TfvcReconcileItemViewModel(TfvcOfflineChange change)
    {
        Change = change;
        IsSelected = true;
    }

    /// <summary>Gets the change.</summary>
    public TfvcOfflineChange Change { get; }

    /// <summary>Gets or sets whether the change is pended.</summary>
    [Reactive]
    public partial bool IsSelected { get; set; }

    /// <summary>Gets the localized change type.</summary>
    public string ChangeText => Change.Kind switch
    {
        TfvcOfflineChangeKind.Edit => TfvcStrings.ChangeEdit,
        TfvcOfflineChangeKind.Add => TfvcStrings.ChangeAdd,
        _ => TfvcStrings.ChangeDelete
    };

    /// <summary>Gets the local path.</summary>
    public string LocalPath => Change.LocalPath;

    /// <summary>Gets the local path with a zero-width space after each separator, so a long path wraps between folders.</summary>
    public string WrappableLocalPath => Change.LocalPath.Replace("\\", "\\​", StringComparison.Ordinal);
}
