using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.Tests.UI;

public sealed class ConfirmDialogTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmReturnsTrueAndCancelReturnsFalse(bool confirm)
    {
        ConfirmDialogViewModel viewModel = new(UiStrings.ConfirmDelete, "Layout 'X' löschen?", UiStrings.Delete);
        Window owner = new();
        ConfirmDialog dialog = new() { DataContext = viewModel };
        try
        {
            owner.Show();
            Task<bool> result = dialog.ShowDialog<bool>(owner);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.Equal(UiStrings.ConfirmDelete, dialog.Title);
            Assert.Equal(UiStrings.Delete, dialog.FindControl<Button>("ConfirmButton")!.Content);
            Assert.Equal(UiStrings.Cancel, dialog.FindControl<Button>("CancelButton")!.Content);

            if (confirm)
            {
                await viewModel.ConfirmCommand.Execute();
            }
            else
            {
                await viewModel.CancelCommand.Execute();
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(confirm, await result.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(dialog.IsVisible);
        }
        finally
        {
            if (dialog.IsVisible)
            {
                dialog.Close();
            }

            owner.Close();
        }
    }

    [Fact]
    public void ConfirmTextFallsBackToOk()
    {
        ConfirmDialogViewModel viewModel = new("Title", "Message");

        Assert.Equal(UiStrings.Ok, viewModel.ConfirmText);
    }

    [AvaloniaFact]
    public async Task DestructiveConfirmationDefaultsToCancel()
    {
        ConfirmDialogViewModel viewModel = new("Discard", "Changes will be lost.", "Discard", isDestructive: true);
        Window owner = new();
        ConfirmDialog dialog = new() { DataContext = viewModel };
        try
        {
            owner.Show();
            Task<bool> result = dialog.ShowDialog<bool>(owner);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.False(dialog.FindControl<Button>("ConfirmButton")!.IsDefault);
            Assert.True(dialog.FindControl<Button>("CancelButton")!.IsDefault);
            Assert.True(dialog.FindControl<Button>("CancelButton")!.IsCancel);

            await viewModel.CancelCommand.Execute();
            Assert.False(await result.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            owner.Close();
        }
    }
}
