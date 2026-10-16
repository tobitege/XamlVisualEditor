using System.Globalization;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Tests.Unit.Extensions;
using SampleThemeProvider = XamlVisualEditor.TestControls.SampleThemeProvider;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

[Collection(nameof(UiLocalizationCollection))]
public sealed class PreviewThemeLabelTests
{
    private const string ProviderId = "sample.palette";
    private static readonly DesignerThemeSelection Harbor = new(ProviderId, "harbor");

    [AvaloniaFact]
    public void SelectionBeforeAnyCatalogShowsThePendingLabelWithoutTheProviderId()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("Pending.axaml");

        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);

        DesignerThemeChoice selected = Assert.IsType<DesignerThemeChoice>(document.DesignSurface.SelectedPreviewTheme);
        Assert.Equal(Harbor, selected.Selection);
        Assert.Equal("harbor (waiting for the preview)", selected.DisplayName);
        Assert.DoesNotContain(ProviderId, selected.DisplayName);
    }

    [AvaloniaFact]
    public async Task CatalogWithTheSelectedThemeReplacesThePendingLabelWithItsName()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("Listed.axaml");
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);
        using PreviewerTcpSession session = await ConnectAsync(document);

        Assert.True(session.TryReceiveThemeCatalog(Message(document, new DesignerTheme("harbor", "Harbor", false))));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal("Harbor", document.DesignSurface.SelectedPreviewTheme?.DisplayName);
        Assert.Equal(Harbor, document.DesignSurface.SelectedPreviewTheme?.Selection);
        Assert.DoesNotContain(document.DesignSurface.PreviewThemes, choice => choice.DisplayName.Contains("waiting", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task CatalogWithoutTheSelectedThemeShowsTheUnavailableLabel()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("Missing.axaml");
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);
        using PreviewerTcpSession session = await ConnectAsync(document);

        Assert.True(session.TryReceiveThemeCatalog(Message(document, new DesignerTheme("clay", "Clay", false))));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal("Unavailable: harbor", document.DesignSurface.SelectedPreviewTheme?.DisplayName);
        Assert.Equal(Harbor, document.DesignSurface.SelectedPreviewTheme?.Selection);
    }

    [AvaloniaFact]
    public async Task PreviewerThatExitsWithoutACatalogShowsTheUnavailableLabel()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("Exited.axaml");
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);
        PreviewerTcpSession session = await ConnectAsync(document);
        Assert.Equal("harbor (waiting for the preview)", document.DesignSurface.SelectedPreviewTheme?.DisplayName);

        session.Dispose();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        DesignerThemeChoice selected = Assert.IsType<DesignerThemeChoice>(document.DesignSurface.SelectedPreviewTheme);
        Assert.Equal("Unavailable: harbor", selected.DisplayName);
        Assert.Equal(Harbor, selected.Selection);
        Assert.Contains(selected, document.DesignSurface.PreviewThemes);
        Assert.DoesNotContain(document.DesignSurface.PreviewThemes, choice => choice.DisplayName.Contains("waiting", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void FailedPreviewerStartShowsTheUnavailableLabelUntilTheNextStart()
    {
        using CultureScope culture = new("en");
        using DesignerDocumentViewModel document = new("StartFailed.axaml");
        document.SetPreviewThemeSelection(Harbor, activatePreviewer: false);

        document.SetPreviewerLaunchError("The previewer could not start.");
        Assert.Equal("Unavailable: harbor", document.DesignSurface.SelectedPreviewTheme?.DisplayName);

        document.SetPreviewerLaunchError(null);
        Assert.Equal("harbor (waiting for the preview)", document.DesignSurface.SelectedPreviewTheme?.DisplayName);
    }

    [AvaloniaTheory]
    [InlineData("en", "Theme default")]
    [InlineData("de", "Projektstandard")]
    public void ProjectDefaultLabelFollowsTheUiCulture(string language, string expected)
    {
        using CultureScope culture = new(language);
        using DesignerDocumentViewModel document = new("ProjectDefault.axaml");

        Assert.Equal(expected, DesignerThemeChoice.ProjectDefault.DisplayName);
        Assert.Same(DesignerThemeChoice.ProjectDefault, document.DesignSurface.SelectedPreviewTheme);
        Assert.Equal(expected, document.DesignSurface.PreviewThemes[0].DisplayName);
        Assert.NotEqual(DesignerThemeChoice.ProjectDefault, new DesignerThemeChoice(null, expected));
    }

    private static async Task<PreviewerTcpSession> ConnectAsync(DesignerDocumentViewModel document)
    {
        PreviewerTcpSession session = new(document.FilePath, null);
        document.PreviewerSession = session;
        await session.SendUpdateXamlAsync("<Canvas xmlns=\"https://github.com/avaloniaui\" />", typeof(SampleThemeProvider).Assembly.Location,
            "/" + document.FilePath, 160, 80, themeRequestId: document.PreviewThemeRequest!.RequestId);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return session;
    }

    private static string Message(DesignerDocumentViewModel document, params DesignerTheme[] themes) =>
        DesignerThemeProtocol.CatalogPrefix + JsonSerializer.Serialize(new DesignerThemeCatalog(document.PreviewThemeRequest!.RequestId,
            new[] { new DesignerThemeProviderCatalog(ProviderId, "Sample", themes) }));

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo? _previous = UiStrings.Culture;

        public CultureScope(string language) => UiStrings.Culture = CultureInfo.GetCultureInfo(language);

        public void Dispose() => UiStrings.Culture = _previous;
    }
}
