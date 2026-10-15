using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using XamlVisualEditor.App.Services;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class DialogAppearanceTests
{
    [AvaloniaFact]
    public void OwnedWindowGetsTheFrameClassAndKeepsItsContent()
    {
        using IDisposable handler = DialogAppearance.Install();
        Window owner = new();
        TextBox content = new();
        Window dialog = new() { Content = content };
        Window independent = new();
        try
        {
            owner.Show();
            independent.Show();
            dialog.Show(owner);

            Assert.Contains(DialogAppearance.FrameClass, dialog.Classes);
            Assert.Same(content, dialog.Content);
            Assert.DoesNotContain(DialogAppearance.FrameClass, independent.Classes);
            Assert.DoesNotContain(DialogAppearance.FrameClass, owner.Classes);
        }
        finally
        {
            dialog.Close();
            independent.Close();
            owner.Close();
        }
    }
}
