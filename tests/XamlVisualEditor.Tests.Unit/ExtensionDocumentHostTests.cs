using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Dock.Model.Controls;
using Dock.Model.Core;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ExtensionDocumentHostTests
{
    [AvaloniaFact]
    public async Task A_document_is_shown_once_per_id_left_out_of_the_layout_and_disposes_its_view_model_when_closed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using MainWindowViewModel viewModel = new();
        IRootDock layout = Assert.IsAssignableFrom<IRootDock>(viewModel.DockLayout);
        using ExtensionViewHostAdapter host = new(viewModel);
        int created = 0;

        object? shown = await host.ShowDocumentAsync("test.document:a", "A (HEAD)", "C:\\repo\\a.txt", Create, ct);
        object? again = await host.ShowDocumentAsync("test.document:a", "Other", null, Create, ct);

        DisposableViewModel document = Assert.IsType<DisposableViewModel>(shown);
        Assert.Same(document, again);
        Assert.Equal(1, created);
        ExtensionDocument dockable = Assert.IsType<ExtensionDocument>(
            XamlEditorDockFactory.FindDockable<ExtensionDocument>(layout, ExtensionDocument.ID_PREFIX + "test.document:a"));
        Assert.Equal("A (HEAD)", dockable.Title);
        Assert.Equal("C:\\repo\\a.txt", DockableToolTipConverter.GetToolTip(dockable, dockable.Title));
        Assert.Same(dockable, XamlEditorDockFactory.FindDockable<IDocumentDock>(layout, "DocumentDock")?.ActiveDockable);
        Assert.Same(document, Assert.IsType<ContentControl>(new XamlVisualEditor.App.ViewLocator().Build(dockable)).Content);

        string path = Path.Combine(Path.GetTempPath(), "xve-extension-document-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.True(viewModel.DockFactory.SaveLayout(layout, path));
            Assert.DoesNotContain(ExtensionDocument.ID_PREFIX, File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Same(dockable, XamlEditorDockFactory.FindDockable<ExtensionDocument>(layout, dockable.Id!));
        }
        finally
        {
            File.Delete(path);
        }

        viewModel.DockFactory.CloseDockable(dockable);

        Assert.True(document.IsDisposed);
        Assert.Null(XamlEditorDockFactory.FindDockable<ExtensionDocument>(layout, dockable.Id!));

        object Create()
        {
            created++;
            return new DisposableViewModel();
        }
    }

    private sealed class DisposableViewModel : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
