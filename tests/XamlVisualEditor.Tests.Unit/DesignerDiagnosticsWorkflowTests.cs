using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using PropertyFixtureControl = XamlVisualEditor.TestControls.PropertyFixtureControl;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignerDiagnosticsWorkflowTests
{
    [AvaloniaFact]
    public async Task ExternalPreviewSuppressesInactiveLocalRenderErrorsButKeepsSyntaxAndHostErrors()
    {
        using DesignerDocumentViewModel document = new("RendererDiagnostics.axaml");
        await document.SyncEngine.LoadAsync("<UserControl xmlns=\"https://github.com/avaloniaui\" />");
        await FlushAsync();
        document.SetRuntimeRenderFailure(new InvalidOperationException("Local renderer failed."));
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Source == "Designer");
        document.UseExternalPreviewer = true;
        await FlushAsync();
        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Source == "Designer");
        using PreviewerTcpSession session = new(document.FilePath, log: null);
        document.PreviewerSession = session;
        session.ReportError("External renderer failed.");
        await FlushAsync();
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Source == "Previewer");
        document.UseExternalPreviewer = false;
        await FlushAsync();
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Source == "Designer");
        document.UseExternalPreviewer = true;
        document.SyncEngine.NotifyTextChanged("<UserControl", SyncSource.CodeEditor);
        await document.SyncEngine.SaveAsync();
        await FlushAsync();
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Source == "XAML");
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Source == "Previewer");
        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Source == "Designer");
    }

    [AvaloniaFact]
    public async Task RejectedGestureKeepsItsDiagnosticAfterRollbackAndClearsItAfterAnEdit()
    {
        const string xaml = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button Canvas.Left=\"10\" Canvas.Top=\"{Binding Position}\" /></Canvas>";
        using DesignerDocumentViewModel document = new("GestureDiagnostics.axaml") { UseExternalPreviewer = true };
        await document.SyncEngine.LoadAsync(xaml);
        await FlushAsync();
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode button = Assert.IsType<MutableAstObjectNode>(Assert.Single(root.Children));
        document.SetSelectedNode(button.Id, SyncSource.DesignSurface);
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 200, 100, new[]
        {
            new DesignerHostNode(root.Id, 1, null, "Canvas", null, 0, 0, 200, 100, true, Layout: new() { Kind = "Canvas" }),
            new DesignerHostNode(button.Id, 2, 1, "Button", null, 10, 20, 80, 30, true, Layout: new() { CanvasLeft = 10, CanvasTop = 20 })
        });
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PreviewerGeometryEditor(document, tree).Nudge(new[] { new DesignItem(button) { HostInstanceId = 2 } }, 1, 1));
        document.SetDesignerInteractionError(error);
        await FlushAsync();
        Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "XVE_EDIT" && diagnostic.Message.Contains("Canvas.Top", StringComparison.Ordinal));
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        Assert.Equal(0, document.SyncEngine.UndoRedo.UndoCount);
        button = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(button.Id));
        using (DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Change content"))
        {
            button.SetPropertyValue("Content", "Updated");
            edit.Commit();
        }
        await FlushAsync();
        Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Code == "XVE_EDIT");
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task ExternalPreviewInsertAndWrapAssignAutomationIdsAndUndoKeepsNoLocalControls()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string xaml = """
            <library:PropertyFixtureControl xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:library="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls" />
            """;
        string path = Path.Combine(Path.GetTempPath(), "xve-automation-ids-" + Guid.NewGuid().ToString("N") + ".axaml");
        Assert.False(File.Exists(path));
        try
        {
            await File.WriteAllTextAsync(path, xaml, cancellationToken);
            TypeMetadataService metadata = new();
            metadata.LoadAssembly(typeof(Control).Assembly.Location);
            metadata.LoadAssembly(typeof(Brush).Assembly.Location);
            metadata.LoadAssembly(typeof(PropertyFixtureControl).Assembly.Location);
            using DesignerDocumentViewModel document = new(path, metadata) { UseExternalPreviewer = true };
            await document.LoadAsync().WaitAsync(cancellationToken);
            using MainWindowViewModel main = new(metadataService: metadata);
            main.Documents.Add(document);
            main.ActiveDocument = document;
            MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
            DesignItem remote = new(root) { HostInstanceId = 1, HostBounds = new Avalonia.Rect(0, 0, 200, 100) };
            document.DesignSurface.SetDesignTree(remote,
                new Dictionary<Guid, DesignItem> { [root.Id] = remote },
                new Dictionary<Control, DesignItem>());
            document.SetSelectedNode(root.Id, SyncSource.TreeView);
            using ShellDesignerHost host = new(main);

            document.DesignSurface.AutoGenerateAutomationIds = true;
            string parentId = root.Id.ToString("D");
            string? insertedId = await host.InsertElementAsync("Button", "https://github.com/avaloniaui", parentId, cancellationToken);
            Assert.NotNull(insertedId);
            MutableAstObjectNode inserted = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(Guid.Parse(insertedId)));
            Assert.Equal("Button_1", inserted.GetPropertyValue("AutomationProperties.AutomationId"));
            string? wrapperId = await host.WrapNodeAsync(insertedId, "Grid", "https://github.com/avaloniaui", cancellationToken);
            Assert.NotNull(wrapperId);
            MutableAstObjectNode wrapper = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(Guid.Parse(wrapperId)));
            Assert.Equal("Grid_1", wrapper.GetPropertyValue("AutomationProperties.AutomationId"));
            Assert.Equal("Button_1", inserted.GetPropertyValue("AutomationProperties.AutomationId"));
            document.SyncEngine.Undo();
            Assert.Null(document.NodeMap.FindById(Guid.Parse(wrapperId)));
            Assert.NotNull(document.NodeMap.FindById(Guid.Parse(insertedId)));
            Assert.Empty(document.DesignSurface.ControlMap);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task FlushAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}
