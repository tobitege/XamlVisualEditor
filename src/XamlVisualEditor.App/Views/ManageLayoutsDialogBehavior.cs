using System;
using System.Reactive;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.App.Views;

/// <summary>Shows the dialog that renames and deletes saved layouts, owned by the main window.</summary>
public sealed class ManageLayoutsDialogBehavior : Behavior<Window>
{
    private IDisposable? _registration;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject!.DataContextChanged += OnDataContextChanged;
        Register();
    }

    protected override void OnDetaching()
    {
        AssociatedObject!.DataContextChanged -= OnDataContextChanged;
        _registration?.Dispose();
        _registration = null;
        base.OnDetaching();
    }

    private void OnDataContextChanged(object? sender, EventArgs args) => Register();

    private void Register()
    {
        _registration?.Dispose();
        _registration = null;
        if (AssociatedObject is not { DataContext: MainWindowViewModel viewModel } owner)
        {
            return;
        }

        _registration = viewModel.ManageLayoutsInteraction.RegisterHandler(async interaction =>
        {
            ManageLayoutsDialogViewModel dialogViewModel = interaction.Input;
            ManageLayoutsDialog dialog = new() { DataContext = dialogViewModel };
            void Close() => dialog.Close();
            dialogViewModel.CloseRequested += Close;
            using IDisposable confirmation = dialogViewModel.ConfirmDeleteInteraction.RegisterHandler(async confirm =>
            {
                ConfirmDialog confirmDialog = new()
                {
                    DataContext = new ConfirmDialogViewModel(
                        UiStrings.ConfirmDelete,
                        string.Format(UiStrings.LayoutDeletePrompt, confirm.Input),
                        UiStrings.Delete)
                };
                confirm.SetOutput(await confirmDialog.ShowDialog<bool>(dialog));
            });
            try
            {
                await dialog.ShowDialog(owner);
            }
            finally
            {
                dialogViewModel.CloseRequested -= Close;
            }

            interaction.SetOutput(RxVoid.Default);
        });
    }
}
