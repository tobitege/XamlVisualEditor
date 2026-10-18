using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>
/// ViewModel for a simple input dialog.
/// </summary>
public sealed partial class InputBoxDialogViewModel : ReactiveObject
{
    public string Title { get; }
    public string Prompt { get; }

    [Reactive]
    public partial string Value { get; set; }

    public Interaction<string?, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> ConfirmCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public InputBoxDialogViewModel(InputBoxOptions options)
    {
        Title = string.IsNullOrWhiteSpace(options.Title) ? UiStrings.Input : options.Title;
        Prompt = options.Prompt ?? string.Empty;
        Value = options.Value ?? string.Empty;

        ConfirmCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(Value));
        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(null));
    }
}

/// <summary>
/// ViewModel for a quick pick dialog.
/// </summary>
public sealed partial class QuickPickDialogViewModel : ReactiveObject
{
    public string Title { get; }

    public ObservableCollection<QuickPickItemViewModel> Items { get; } = new();

    [Reactive]
    public partial QuickPickItemViewModel? SelectedItem { get; set; }

    public Interaction<QuickPickItem?, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> ConfirmCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public QuickPickDialogViewModel(string? title, IReadOnlyList<QuickPickItem> items)
    {
        Title = string.IsNullOrWhiteSpace(title) ? UiStrings.Select : title;

        foreach (QuickPickItem item in items)
        {
            Items.Add(new QuickPickItemViewModel(item));
        }

        SelectedItem = Items.FirstOrDefault();

        IObservable<bool> canConfirm = this.WhenAnyValue(x => x.SelectedItem)
            .Select(item => item is not null);

        ConfirmCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(SelectedItem?.Item), canConfirm);
        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(null));
    }
}

/// <summary>
/// ViewModel for a single quick pick item.
/// </summary>
public sealed partial class QuickPickItemViewModel
{
    public QuickPickItemViewModel(QuickPickItem item)
    {
        Item = item;
    }

    public QuickPickItem Item { get; }

    public string Label => Item.Label;

    public string? Description => Item.Description;

    public string? Detail => Item.Detail;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Item.Description);

    public bool HasDetail => !string.IsNullOrWhiteSpace(Item.Detail);
}

/// <summary>
/// ViewModel for a simple message dialog.
/// </summary>
public sealed partial class MessageDialogViewModel : ReactiveObject
{
    public string Title { get; }
    public string Message { get; }

    public Interaction<bool, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> ConfirmCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public MessageDialogViewModel(string title, string message)
    {
        Title = title;
        Message = message;

        ConfirmCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(true));
        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(false));
    }
}

/// <summary>
/// ViewModel for a modal, indeterminate progress dialog. The close interaction carries true when the user cancelled.
/// </summary>
public sealed class ProgressDialogViewModel : ReactiveObject
{
    public string Title { get; }
    public string Message { get; }
    public string CancelText { get; }

    private bool _canCancel;

    /// <summary>
    /// Gets whether the user can cancel: from the start, or once the view called <see cref="AllowCancel"/> after
    /// <see cref="CancelDelay"/>. Until then the cancel button is hidden and the window button does nothing.
    /// </summary>
    public bool CanCancel
    {
        get => _canCancel;
        private set => this.RaiseAndSetIfChanged(ref _canCancel, value);
    }

    /// <summary>Gets how long the shown dialog waits before the user can cancel.</summary>
    public TimeSpan CancelDelay { get; }

    /// <summary>Lets the user cancel; the view calls it once <see cref="CancelDelay"/> elapsed.</summary>
    public void AllowCancel() => CanCancel = true;

    public Interaction<bool, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public ProgressDialogViewModel(string title, string message, string? cancelText = null, TimeSpan cancelDelay = default)
    {
        Title = title;
        Message = message;
        CancelText = string.IsNullOrWhiteSpace(cancelText) ? UiStrings.Cancel : cancelText;
        CancelDelay = cancelDelay > TimeSpan.Zero ? cancelDelay : TimeSpan.Zero;
        CanCancel = CancelDelay == TimeSpan.Zero;
        CancelCommand = ReactiveCommand.CreateFromTask(
            async () => await CloseInteraction.Handle(true),
            this.WhenAnyValue(x => x.CanCancel));
    }
}

/// <summary>
/// ViewModel for a yes/no confirmation dialog. The close interaction carries true when the
/// user confirmed and false when the dialog was cancelled.
/// </summary>
public sealed partial class ConfirmDialogViewModel : ReactiveObject
{
    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }
    public bool IsDestructive { get; }
    public bool IsConfirmDefault => !IsDestructive;

    public Interaction<bool, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> ConfirmCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public ConfirmDialogViewModel(string title, string message, string? confirmText = null, bool isDestructive = false,
        string? cancelText = null)
    {
        Title = title;
        Message = message;
        ConfirmText = string.IsNullOrWhiteSpace(confirmText) ? UiStrings.Ok : confirmText;
        CancelText = string.IsNullOrWhiteSpace(cancelText) ? UiStrings.Cancel : cancelText;
        IsDestructive = isDestructive;

        ConfirmCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(true));
        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(false));
    }
}
