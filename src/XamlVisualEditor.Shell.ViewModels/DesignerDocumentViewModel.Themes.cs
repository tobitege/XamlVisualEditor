using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Core;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class DesignerDocumentViewModel
{
    private DesignerThemeRequest? _previewThemeRequest;
    private bool _updatingThemeChoices;

    public DesignerThemeRequest? PreviewThemeRequest
    {
        get => _previewThemeRequest;
        private set => this.RaiseAndSetIfChanged(ref _previewThemeRequest, value);
    }

    public void SetPreviewThemeProviders(IReadOnlyList<DesignerThemeProviderRegistration> providers)
    {
        if (IsDisposed || providers.SequenceEqual(PreviewThemeRequest?.Providers ?? Array.Empty<DesignerThemeProviderRegistration>())) return;
        PreviewThemeRequest = new(Guid.NewGuid(), providers.ToArray(), PreviewThemeRequest?.Selection);
        DesignSurface.HasPreviewThemeProviders = providers.Count > 0 || PreviewThemeRequest.Selection is not null;
        DesignSurface.HasPreviewThemeCatalog = false;
        DesignSurface.PreviewThemeStatus = UiStrings.PreviewThemeLoadHint;
        _previewThemeDiagnostics = Array.Empty<LanguageDiagnostic>();
        PublishDesignerDiagnostics();
    }

    /// <summary>Selects the preview theme; a theme derived from the editor options does not switch to the external previewer.</summary>
    public void SetPreviewThemeSelection(DesignerThemeSelection? selection, bool activatePreviewer = true)
    {
        if (IsDisposed || PreviewThemeRequest?.Selection == selection) return;
        PreviewThemeRequest = new(Guid.NewGuid(), PreviewThemeRequest?.Providers ?? Array.Empty<DesignerThemeProviderRegistration>(), selection);
        DesignSurface.HasPreviewThemeProviders = PreviewThemeRequest.Providers.Count > 0 || selection is not null;
        DesignerThemeChoice? choice = DesignSurface.PreviewThemes.FirstOrDefault(item => item.Selection == selection);
        if (choice is null)
        {
            choice = new(selection, $"{selection!.ProviderId}/{selection.ThemeId}");
            DesignSurface.PreviewThemes.Add(choice);
        }
        DesignSurface.SelectedPreviewTheme = choice;
        if (selection is not null && activatePreviewer) UseExternalPreviewer = true;
    }

    private void InitializeDesignerThemes()
    {
        _disposables.Add(DesignSurface.WhenAnyValue(surface => surface.SelectedPreviewTheme).Skip(1).Subscribe(choice =>
        {
            if (IsDisposed || _updatingThemeChoices || choice is null || PreviewThemeRequest is not { } current
                || choice.Selection == current.Selection) return;
            PreviewThemeRequest = current with { RequestId = Guid.NewGuid(), Selection = choice.Selection };
            if (choice.Selection is not null) UseExternalPreviewer = true;
        }));
        _disposables.Add(Observable.Switch(this.WhenAnyValue(document => document.PreviewerSession)
            .Select(session => session is null ? Observable.Return<DesignerThemeCatalog?>(null)
                : Observable.Create<DesignerThemeCatalog?>(observer =>
                {
                    void OnCatalog(DesignerThemeCatalog? catalog) => observer.OnNext(catalog);
                    session.ThemeCatalogChanged += OnCatalog;
                    observer.OnNext(session.LastThemeCatalog);
                    return Disposable.Create(() => session.ThemeCatalogChanged -= OnCatalog);
                }))
            ).WitnessOn(RxSchedulers.MainThreadScheduler).Subscribe(ApplyThemeCatalog));
    }

    private void ApplyThemeCatalog(DesignerThemeCatalog? catalog)
    {
        if (IsDisposed) return;
        if (catalog is null)
        {
            if (PreviewerSession is null) DesignSurface.HasPreviewThemeCatalog = false;
            return;
        }
        if (catalog.RequestId != PreviewThemeRequest?.RequestId) return;
        DesignerThemeSelection? selection = PreviewThemeRequest.Selection;
        _previewThemeDiagnostics = catalog.Providers.Where(provider => provider.Error is not null && provider.Id != selection?.ProviderId)
            .Select(provider => CreateDesignerDiagnostic(string.Format(UiStrings.PreviewThemeProviderError, provider.Name, provider.Error),
                DiagnosticSeverity.Warning, "Themes", "XVE_PREVIEW_THEME", 1, 1, 1)).ToArray();
        PublishDesignerDiagnostics();
        List<DesignerThemeChoice> choices = new() { DesignerThemeChoice.ProjectDefault };
        foreach (DesignerThemeProviderCatalog provider in catalog.Providers.OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase))
            foreach (DesignerTheme theme in provider.Themes.OrderBy(theme => theme.Name, StringComparer.OrdinalIgnoreCase))
                choices.Add(new(new(provider.Id, theme.Id), catalog.Providers.Count > 1 ? $"{provider.Name}: {theme.Name}" : theme.Name));
        DesignerThemeChoice? selected = choices.FirstOrDefault(choice => choice.Selection == selection);
        if (selected is null)
        {
            selected = new(selection, string.Format(UiStrings.PreviewThemeUnavailable, $"{selection!.ProviderId}/{selection.ThemeId}"));
            choices.Add(selected);
        }
        _updatingThemeChoices = true;
        try
        {
            DesignSurface.PreviewThemes.Clear();
            foreach (DesignerThemeChoice choice in choices) DesignSurface.PreviewThemes.Add(choice);
            DesignSurface.SelectedPreviewTheme = selected;
        }
        finally { _updatingThemeChoices = false; }
        DesignSurface.HasPreviewThemeCatalog = true;
        DesignSurface.PreviewThemeStatus = string.Format(UiStrings.PreviewThemePreviewOnly, selected.DisplayName);
    }
}
