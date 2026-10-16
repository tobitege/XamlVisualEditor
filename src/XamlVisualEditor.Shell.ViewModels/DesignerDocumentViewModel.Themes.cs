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
    private DesignerThemeCatalog? _previewThemeCatalog;
    // The choice added for a selection that no catalog has listed yet; a received catalog replaces it.
    private DesignerThemeChoice? _pendingThemeChoice;
    private bool _updatingThemeChoices;

    public DesignerThemeRequest? PreviewThemeRequest
    {
        get => _previewThemeRequest;
        private set => this.RaiseAndSetIfChanged(ref _previewThemeRequest, value);
    }

    /// <summary>
    /// The last catalog the preview host reported for the current providers, or null before one arrived. It stays
    /// set when a later request changes only the selection, because the offered themes depend on the providers.
    /// </summary>
    public DesignerThemeCatalog? PreviewThemeCatalog
    {
        get => _previewThemeCatalog;
        private set => this.RaiseAndSetIfChanged(ref _previewThemeCatalog, value);
    }

    public void SetPreviewThemeProviders(IReadOnlyList<DesignerThemeProviderRegistration> providers)
    {
        if (IsDisposed || providers.SequenceEqual(PreviewThemeRequest?.Providers ?? Array.Empty<DesignerThemeProviderRegistration>())) return;
        PreviewThemeRequest = new(Guid.NewGuid(), providers.ToArray(), PreviewThemeRequest?.Selection);
        PreviewThemeCatalog = null;
        DesignSurface.HasPreviewThemeProviders = providers.Count > 0 || PreviewThemeRequest.Selection is not null;
        DesignSurface.HasPreviewThemeCatalog = false;
        DesignSurface.PreviewThemeStatus = UiStrings.PreviewThemeLoadHint;
        _previewThemeDiagnostics = Array.Empty<LanguageDiagnostic>();
        PublishDesignerDiagnostics();
        // The registered providers decide whether the label of a pending selection names its provider.
        UpdatePendingThemeChoice();
    }

    /// <summary>Selects the preview theme; a theme derived from the editor options does not switch to the external previewer.</summary>
    public void SetPreviewThemeSelection(DesignerThemeSelection? selection, bool activatePreviewer = true)
    {
        if (IsDisposed || PreviewThemeRequest?.Selection == selection) return;
        PreviewThemeRequest = new(Guid.NewGuid(), PreviewThemeRequest?.Providers ?? Array.Empty<DesignerThemeProviderRegistration>(), selection);
        DesignSurface.HasPreviewThemeProviders = PreviewThemeRequest.Providers.Count > 0 || selection is not null;
        DesignerThemeChoice? choice = DesignSurface.PreviewThemes.FirstOrDefault(item => item.Selection == selection);
        bool uncataloged = choice is null;
        if (choice is null)
        {
            choice = CreateUncatalogedThemeChoice(selection!, !CanPreviewerReportThemes);
            DesignSurface.PreviewThemes.Add(choice);
        }
        DesignSurface.SelectedPreviewTheme = choice;
        RemovePendingThemeChoice(choice);
        if (uncataloged) _pendingThemeChoice = choice;
        if (selection is not null && activatePreviewer) UseExternalPreviewer = true;
    }

    /// <summary>
    /// Removes the entry added for an uncataloged selection once another entry is selected, so that
    /// <see cref="_pendingThemeChoice"/> only ever refers to the selected entry.
    /// </summary>
    private void RemovePendingThemeChoice(DesignerThemeChoice selected)
    {
        if (_pendingThemeChoice is not { } pending || ReferenceEquals(pending, selected)) return;
        _pendingThemeChoice = null;
        _updatingThemeChoices = true;
        try { DesignSurface.PreviewThemes.Remove(pending); }
        finally { _updatingThemeChoices = false; }
    }

    /// <summary>
    /// False once the previewer process exited or failed to start; until a previewer starts again, no catalog can list
    /// the pending selection.
    /// </summary>
    private bool CanPreviewerReportThemes => PreviewerState is not (PreviewerState.Exited or PreviewerState.Failed);

    private DesignerThemeChoice CreateUncatalogedThemeChoice(DesignerThemeSelection selection, bool unavailable) =>
        new(selection, string.Format(unavailable ? UiStrings.PreviewThemeUnavailable : UiStrings.PreviewThemePending,
            GetUncatalogedThemeLabel(selection)));

    /// <summary>
    /// Names a selection that no catalog lists by its theme ID. The ID alone is ambiguous when more than one provider is
    /// registered or when the selection names a provider other than the single registered one, because another provider
    /// may offer the same ID; then the label starts with the provider name from the registration, or with the provider
    /// ID when no registration names it, in the "Provider: theme" form of the catalog entries. Without any registered
    /// provider the selection is the only theme in question and keeps the bare ID.
    /// </summary>
    private string GetUncatalogedThemeLabel(DesignerThemeSelection selection)
    {
        IReadOnlyList<DesignerThemeProviderRegistration> providers =
            PreviewThemeRequest?.Providers ?? Array.Empty<DesignerThemeProviderRegistration>();
        if (providers.Count == 0 || providers.Count == 1 && providers[0].Id == selection.ProviderId) return selection.ThemeId;
        string provider = providers.FirstOrDefault(registration => registration.Id == selection.ProviderId)?.Name
            ?? selection.ProviderId;
        return $"{provider}: {selection.ThemeId}";
    }

    /// <summary>Relabels the selected entry that no catalog has listed yet when the previewer state changes.</summary>
    private void UpdatePendingThemeChoice()
    {
        if (_pendingThemeChoice is not { Selection: { } selection } pending) return;
        int index = DesignSurface.PreviewThemes.IndexOf(pending);
        if (index < 0)
        {
            _pendingThemeChoice = null;
            return;
        }
        DesignerThemeChoice updated = CreateUncatalogedThemeChoice(selection, !CanPreviewerReportThemes);
        if (updated == pending) return;
        _pendingThemeChoice = updated;
        _updatingThemeChoices = true;
        try
        {
            DesignSurface.PreviewThemes[index] = updated;
            DesignSurface.SelectedPreviewTheme = updated;
        }
        finally { _updatingThemeChoices = false; }
    }

    private void InitializeDesignerThemes()
    {
        _disposables.Add(DesignSurface.WhenAnyValue(surface => surface.SelectedPreviewTheme).Skip(1).Subscribe(choice =>
        {
            if (IsDisposed || _updatingThemeChoices || choice is null || PreviewThemeRequest is not { } current
                || choice.Selection == current.Selection) return;
            PreviewThemeRequest = current with { RequestId = Guid.NewGuid(), Selection = choice.Selection };
            RemovePendingThemeChoice(choice);
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
            selected = CreateUncatalogedThemeChoice(selection!, unavailable: true);
            choices.Add(selected);
        }
        _pendingThemeChoice = null;
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
        // Raised last: a listener may replace a selection the catalog does not offer.
        PreviewThemeCatalog = catalog;
    }
}
