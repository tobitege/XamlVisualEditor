using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using SampleThemeProvider = XamlVisualEditor.TestControls.SampleThemeProvider;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignerThemeWorkflowTests
{
    [AvaloniaFact]
    public void ProviderRequestLoadsAsLiteralMetadataWithoutExecutingProviderCodeInTheEditor()
    {
        const string xaml = "<UserControl xmlns=\"https://github.com/avaloniaui\"><Button Content=\"Untouched\" /></UserControl>";
        MutableAstDocument document = Assert.IsType<MutableAstDocument>(new XamlParsingService().Parse(xaml).Document);
        XamlSerializationService serializer = new();
        string original = serializer.Serialize(document);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        DesignerThemeProviderRegistration provider = new("sample.palette", "Sample", typeof(SampleThemeProvider).Assembly.Location,
            "NotLoadedInEditor.ThemeProvider");
        DesignerThemeRequest request = new(Guid.NewGuid(), new[] { provider }, new(provider.Id, "harbor"));
        string prepared = new RuntimeDesignRenderer(metadata).PrepareXaml(document, themeRequest: request);
        DesignerThemeRequest decoded = Assert.IsType<DesignerThemeRequest>(DesignerThemeProtocol.ReadRequest(XDocument.Parse(prepared)));
        Assert.Equal(request.RequestId, decoded.RequestId);
        Assert.Equal(provider, Assert.Single(decoded.Providers));
        Assert.Equal(request.Selection, decoded.Selection);
        UserControl root = Assert.IsType<UserControl>(AvaloniaRuntimeXamlLoader.Load(prepared, typeof(Control).Assembly, null,
            new Uri("avares://XamlVisualEditor.Tests.Unit/ThemeRequest.axaml"), true));
        Assert.Equal(JsonSerializer.Serialize(request), DesignPreviewSettings.GetThemeRequest(root));
        Assert.Equal("Untouched", Assert.IsType<Button>(root.Content).Content);
        Assert.Equal(original, serializer.Serialize(document));
    }

    [AvaloniaFact]
    public async Task RegisteredThemesPopulateTheStandardComboAndIgnoreStaleCatalogsWithoutEditingXaml()
    {
        DesignerThemeProviderRegistration provider = new("sample.palette", "Sample", typeof(SampleThemeProvider).Assembly.Location,
            typeof(SampleThemeProvider).FullName!);
        ExtensionContributionRegistry registry = new();
        IDisposable oldRegistration = registry.RegisterDesignerThemeProviders("sample.extension", new[] { provider });
        // Disposing a registration frees its provider ID, so the same ID can be registered again.
        oldRegistration.Dispose();
        using IDisposable registration = registry.RegisterDesignerThemeProviders("sample.extension", new[] { provider });
        // Disposing the old handle a second time leaves the newer registration in place.
        oldRegistration.Dispose();
        Assert.Equal(provider, Assert.Single(registry.DesignerThemeProviders));
        using DesignerDocumentViewModel document = new("ThemeChoice.axaml");
        const string xaml = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button /></Canvas>";
        await document.SyncEngine.LoadAsync(xaml);
        document.SetPreviewThemeProviders(registry.DesignerThemeProviders);
        Guid first = document.PreviewThemeRequest!.RequestId;
        using PreviewerTcpSession session = new(document.FilePath, null);
        document.PreviewerSession = session;
        await session.SendUpdateXamlAsync(xaml, provider.AssemblyPath, "/ThemeChoice.axaml", 160, 80, themeRequestId: first);
        DesignerThemeCatalog Catalog(Guid id) => new(id, new[] { new DesignerThemeProviderCatalog(provider.Id, provider.Name,
            new[] { new DesignerTheme("harbor", "Harbor", true), new DesignerTheme("clay", "Clay", false) }) });
        string Message(Guid id) => DesignerThemeProtocol.CatalogPrefix + JsonSerializer.Serialize(Catalog(id));
        DesignerViewportToolbar toolbar = new() { DataContext = document.DesignSurface };
        Window window = new() { Content = toolbar, Width = 700, Height = 160 };
        try
        {
            window.Show();
            Assert.True(session.TryReceiveThemeCatalog(Message(first)));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            ComboBox combo = toolbar.FindControl<ComboBox>("LibraryThemeSelector")!;
            Assert.True(combo.IsVisible);
            Assert.True(combo.IsEnabled);
            Assert.Equal(new[] { "Theme default", "Clay", "Harbor" }, document.DesignSurface.PreviewThemes.Select(theme => theme.DisplayName));
            combo.SelectedIndex = 2;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(new DesignerThemeSelection(provider.Id, "harbor"), document.PreviewThemeRequest.Selection);
            Assert.True(document.UseExternalPreviewer);
            Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
            Assert.Equal(0, document.SyncEngine.UndoRedo.UndoCount);
            Guid second = document.PreviewThemeRequest.RequestId;
            Assert.NotEqual(first, second);
            await session.SendUpdateXamlAsync(xaml, provider.AssemblyPath, "/ThemeChoice.axaml", 160, 80, themeRequestId: second);
            session.TryReceiveThemeCatalog(Message(first));
            Assert.Null(session.LastThemeCatalog);
            session.TryReceiveThemeCatalog(Message(second));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(second, document.PreviewThemeRequest.RequestId);
            Assert.Equal("harbor", document.DesignSurface.SelectedPreviewTheme?.Selection?.ThemeId);
            combo.SelectedIndex = 0;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Null(document.PreviewThemeRequest.Selection);
            Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
            Guid recovery = document.PreviewThemeRequest.RequestId;
            await session.SendUpdateXamlAsync(xaml, provider.AssemblyPath, "/ThemeChoice.axaml", 160, 80, themeRequestId: recovery);
            DesignerThemeCatalog partial = new(recovery, new[]
            {
                Catalog(recovery).Providers[0],
                new DesignerThemeProviderCatalog("missing.provider", "Missing", Array.Empty<DesignerTheme>(), "Assembly unavailable")
            });
            session.TryReceiveThemeCatalog(DesignerThemeProtocol.CatalogPrefix + JsonSerializer.Serialize(partial));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(combo.IsEnabled);
            Assert.Contains(document.DesignSurface.PreviewThemes, choice => choice.Selection?.ThemeId == "harbor");
            Assert.Contains(document.Diagnostics, diagnostic => diagnostic.Code == "XVE_PREVIEW_THEME" && diagnostic.Message.Contains("Assembly unavailable"));
            session.TryReceiveThemeCatalog(Message(recovery));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.DoesNotContain(document.Diagnostics, diagnostic => diagnostic.Code == "XVE_PREVIEW_THEME");
        }
        finally { window.Close(); }
        registration.Dispose();
        Assert.Empty(registry.DesignerThemeProviders);
    }
}
