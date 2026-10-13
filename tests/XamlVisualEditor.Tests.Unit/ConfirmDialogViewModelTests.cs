using System.Reactive.Threading.Tasks;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ConfirmDialogViewModelTests
{
    [Fact]
    public void CancelText_Defaults_To_Cancel()
    {
        ConfirmDialogViewModel dialog = new("Title", "Message", "Convert");

        Assert.Equal(UiStrings.Cancel, dialog.CancelText);
    }

    [Fact]
    public void CancelText_Uses_The_Requested_Label()
    {
        ConfirmDialogViewModel dialog = new("Title", "Message", "Convert", cancelText: "Open as C#");

        Assert.Equal("Open as C#", dialog.CancelText);
    }

    [Fact]
    public async Task ProgressDialog_Cancel_Closes_With_True()
    {
        ProgressDialogViewModel dialog = new("Title", "Message");
        bool? closedWith = null;
        using IDisposable handler = dialog.CloseInteraction.RegisterHandler(context =>
        {
            closedWith = context.Input;
            context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
        });

        await dialog.CancelCommand.Execute().ToTask();

        Assert.Equal(UiStrings.Cancel, dialog.CancelText);
        Assert.True(closedWith);
    }
}
