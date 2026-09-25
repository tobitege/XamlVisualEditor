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
}
