using System.Reactive.Disposables;
using System.Reactive.Threading.Tasks;
using XamlVisualEditor.Acp;
using XamlVisualEditor.Extensions;
using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using PrimitivesLinq = ReactiveUI.Primitives.LinqExtensions;
using ReactiveUI;

namespace XamlVisualEditor.AcpExtension;

public sealed class AcpExtension : IXveExtension
{
    private const string ViewId = "acp.panel";
    private readonly IAcpService _acpService;
    private readonly IAcpProfileStore _profileStore;
    private readonly ISecretStore _secretStore;
    private readonly IAcpOAuthDeviceFlowService _oauthService;
    private readonly IWorkspaceInfo _workspaceInfo;
    private AcpToolViewModel? _viewModel;

    public AcpExtension(
        IAcpService acpService,
        IAcpProfileStore profileStore,
        ISecretStore secretStore,
        IAcpOAuthDeviceFlowService oauthService,
        IWorkspaceInfo workspaceInfo)
    {
        _acpService = acpService;
        _profileStore = profileStore;
        _secretStore = secretStore;
        _oauthService = oauthService;
        _workspaceInfo = workspaceInfo;
    }

    public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        _viewModel = new AcpToolViewModel(
            _acpService,
            _profileStore,
            _secretStore,
            _oauthService,
            () => _workspaceInfo.WorkspacePath);

        ExtensionViewContribution view = new(
            ViewId,
            "ACP",
            ExtensionViewType.Custom,
            ExtensionViewLocation.Right,
            30);

        IDisposable viewRegistration = context.Contributions.RegisterViews(
            context.ExtensionId,
            new[] { view });
        IDisposable providerRegistration = context.Views.RegisterCustomViewProvider(
            ViewId,
            new AcpViewProvider(_viewModel));

        context.Subscriptions.Add(viewRegistration);
        context.Subscriptions.Add(providerRegistration);
        context.Subscriptions.Add(Disposable.Create(() => _viewModel?.Dispose()));

        _acpService.SetPermissionHandler((request, ct) =>
        {
            if (ct.IsCancellationRequested || _viewModel is null)
            {
                return Task.FromResult(AcpPermissionOutcome.Cancelled());
            }

            // The agent asks from a background thread; the permission dialog has to be raised on the main thread.
            return PrimitivesLinq.SubscribeOn(Signal.FromTask(_ => _viewModel.PermissionInteraction.Handle(request),
                RxSchedulers.MainThreadScheduler), RxSchedulers.MainThreadScheduler).ToTask(ct);
        });
        context.Subscriptions.Add(Disposable.Create(() => _acpService.SetPermissionHandler(null)));

        return Task.CompletedTask;
    }

    private sealed class AcpViewProvider : ICustomViewProvider
    {
        private readonly AcpToolViewModel _viewModel;

        public AcpViewProvider(AcpToolViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public object? CreateViewModel()
        {
            return _viewModel;
        }
    }
}
