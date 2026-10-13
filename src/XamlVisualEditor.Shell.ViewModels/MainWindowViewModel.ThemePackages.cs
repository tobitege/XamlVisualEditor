using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions.Hosting;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly HashSet<string> _changingThemePackages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DesignerDocumentViewModel>> _themePackageRestarts = new(StringComparer.OrdinalIgnoreCase);
    private bool _themePackagesDisposed;

    private void InitializeThemePackages(IExtensionManager? manager)
    {
        if (manager is not IExtensionThemeProviderLifecycle lifecycle) return;
        lifecycle.ThemeProvidersChanging += BeforeThemePackageChangeAsync;
        lifecycle.ThemeProvidersChanged += AfterThemePackageChangeAsync;
        _disposables.Add(Disposable.Create(() =>
        {
            _themePackagesDisposed = true;
            lifecycle.ThemeProvidersChanging -= BeforeThemePackageChangeAsync;
            lifecycle.ThemeProvidersChanged -= AfterThemePackageChangeAsync;
            _changingThemePackages.Clear();
            _themePackageRestarts.Clear();
        }));
    }

    private static bool UsesThemePackage(DesignerDocumentViewModel document, string extensionId)
    {
        string prefix = extensionId + "/";
        return document.PreviewThemeRequest is { } request && (request.Providers.Any(provider => provider.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || request.Selection?.ProviderId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);
    }

    private Task BeforeThemePackageChangeAsync(string extensionId, CancellationToken ct) => Signal.Start(() =>
    {
        if (_themePackagesDisposed) return;
        _changingThemePackages.Add(extensionId);
        List<DesignerDocumentViewModel> documents = Documents.OfType<DesignerDocumentViewModel>()
            .Where(document => document.UseExternalPreviewer && !document.IsDisposed && UsesThemePackage(document, extensionId)).ToList();
        _themePackageRestarts[extensionId] = documents;
        foreach (DesignerDocumentViewModel document in documents)
        {
            _previewerLaunchService.StopPreviewer(document.FilePath);
            document.PreviewerSession = null;
        }
    }, RxSchedulers.MainThreadScheduler).ToTask(ct);

    private async Task AfterThemePackageChangeAsync(string extensionId, CancellationToken ct)
    {
        List<DesignerDocumentViewModel> documents = await Signal.Start(() =>
        {
            _changingThemePackages.Remove(extensionId);
            _themePackageRestarts.Remove(extensionId, out List<DesignerDocumentViewModel>? pending);
            if (_themePackagesDisposed) return new List<DesignerDocumentViewModel>();
            foreach (DesignerDocumentViewModel document in Documents.OfType<DesignerDocumentViewModel>()) ConfigurePreviewThemes(document);
            return pending ?? new List<DesignerDocumentViewModel>();
        }, RxSchedulers.MainThreadScheduler).ToTask(ct).ConfigureAwait(false);
        foreach (DesignerDocumentViewModel document in documents)
        {
            await Signal.FromTask(async _ =>
            {
                if (_themePackagesDisposed || document.IsDisposed || !document.UseExternalPreviewer || !Documents.Contains(document)) return RxVoid.Default;
                try { await StartPreviewerForDocumentAsync(document); }
                catch (Exception error) { if (!document.IsDisposed) document.SetPreviewerLaunchError(error.Message); }
                return RxVoid.Default;
            }, RxSchedulers.MainThreadScheduler).ToTask(ct).ConfigureAwait(false);
        }
    }
}
