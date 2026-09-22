using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ReactiveUI;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core;
using XamlVisualEditor.FileExplorerExtension.ViewModels;
using XamlVisualEditor.FileExplorerExtension.Views;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class DialogEscapeTests
{
    [AvaloniaFact]
    public async Task EscapeCancelsDialogsWithoutReturningPendingInputOrSelectedActions()
    {
        MessageDialogViewModel message = new("Message", "Information");
        await AssertCancel(new MessageDialog { DataContext = message }, message.CloseInteraction, false);

        CodeActionPickerDialogViewModel action = new(new CodeActionPickerRequest("Action",
            new[] { new LanguageCodeAction { Title = "Apply a change" } }));
        Assert.NotNull(action.SelectedItem);
        await AssertCancel(new CodeActionPickerDialog { DataContext = action }, action.CloseInteraction, null);

        DefinitionPickerDialogViewModel definition = new(new DefinitionPickerRequest("Definition", Array.Empty<ReferenceLocationViewModel>()));
        await AssertCancel(new DefinitionPickerDialog { DataContext = definition }, definition.CloseInteraction, null);

        CommandPaletteDialogViewModel command = new(new CommandPaletteRequest("Command", Array.Empty<ExtensionCommandPaletteItemViewModel>()));
        await AssertCancel(new CommandPaletteDialog { DataContext = command }, command.CloseInteraction, null);

        OpenFolderDialogViewModel folder = new(Path.Combine(Path.GetTempPath(), "pending-folder"));
        await AssertCancel(new OpenFolderDialog { DataContext = folder }, folder.CloseInteraction, null);
    }

    private static async Task AssertCancel<T>(Window dialog, Interaction<T, Unit> interaction, T expected)
    {
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        Window owner = new();
        using IDisposable handler = interaction.RegisterHandler(context =>
        {
            dialog.Close(context.Input);
            context.SetOutput(Unit.Default);
        });
        try
        {
            owner.Show();
            Task<T> closed = dialog.ShowDialog<T>(owner);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Equal(expected, await closed.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            dialog.Close();
            owner.Close();
            Application.Current.Styles.Remove(theme);
        }
    }
}
