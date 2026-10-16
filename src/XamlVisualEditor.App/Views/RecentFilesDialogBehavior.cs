using System.Reactive;
using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;
using ReactiveUI;
using XamlVisualEditor.Shell.ViewModels;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.App.Views;

/// <summary>Connects the dialog request with the owning window.</summary>
public sealed class RecentFilesDialogBehavior : Behavior<Window>
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
        if (AssociatedObject is not { DataContext: MainWindowViewModel viewModel } owner) return;
        _registration = viewModel.RecentFilesInteraction.RegisterHandler(async interaction =>
        {
            RecentFilesDialog dialog = new() { DataContext = interaction.Input };
            void Close() => dialog.Close();
            interaction.Input.CloseRequested += Close;
            try { await dialog.ShowDialog(owner); }
            finally { interaction.Input.CloseRequested -= Close; }
            interaction.SetOutput(RxVoid.Default);
        });
    }
}
