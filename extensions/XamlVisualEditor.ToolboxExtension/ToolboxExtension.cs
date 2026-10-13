using XamlVisualEditor.Extensions;

using XamlVisualEditor.Core.Localization;
using System.Reactive.Linq;
using ReactiveUI;

namespace XamlVisualEditor.ToolboxExtension;

public sealed class ToolboxExtension : IXveExtension
{
    private const string InsertButtonCommandId = "toolbox.insertButton";
    private const string InsertSelectedCommandId = "toolbox.insertSelected";
    private const string ToolboxViewId = "toolbox.panel";
    private const string ToggleViewCommandId = "toolbox.toggleView";
    private const string ShowTaskCommandId = "toolbox.showLoadTask";

    public async Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        ToolboxPanelViewModel viewModel = new(context.Commands, context.Settings, context.ControlCatalog,
            workspaceInfo: context.WorkspaceInfo, storage: context.Storage, designer: context.Designer);
        context.Subscriptions.Add(viewModel);
        await viewModel.InitializePreferencesAsync(cancellationToken);
        if (context.ControlCatalog is not null)
            await viewModel.ReloadAsync(cancellationToken, includeProjectControls: false, forceRefresh: false);

        context.Subscriptions.Add(context.Commands.Register(InsertButtonCommandId, _ => InsertButtonAsync(context, cancellationToken)));
        context.Subscriptions.Add(context.Commands.Register(InsertSelectedCommandId, commandContext =>
            InsertSelectedAsync(context, commandContext, cancellationToken)));
        context.Subscriptions.Add(context.Commands.Register(ToggleViewCommandId, _ =>
            context.ViewHost.ToggleAsync(ToolboxViewId, CancellationToken.None)));
        context.Subscriptions.Add(context.Commands.Register(ShowTaskCommandId, _ =>
            context.ViewHost.ShowAsync(ToolboxViewId, CancellationToken.None)));

        context.Subscriptions.Add(context.Contributions.RegisterViews(
            context.ExtensionId,
            new[]
            {
                new ExtensionViewContribution(
                    ToolboxViewId,
                    UiStrings.Toolbox,
                    ExtensionViewType.Custom,
                    ExtensionViewLocation.Left,
                    12,
                    ActivateByDefault: true)
            }));

        context.Subscriptions.Add(context.Views.RegisterCustomViewProvider(
            ToolboxViewId,
            new ToolboxPanelViewProvider(viewModel)));

        context.Subscriptions.Add(context.CommandMetadata.Register(
            InsertButtonCommandId,
            new CommandMetadata(
                Title: UiStrings.ToolboxInsertButton,
                Category: UiStrings.Toolbox,
                When: "hasWorkspace && hasDesignerDocument",
                Priority: 50)));

        context.Subscriptions.Add(context.CommandMetadata.Register(
            InsertSelectedCommandId,
            new CommandMetadata(
                Title: UiStrings.ToolboxInsertSelected,
                Category: UiStrings.Toolbox,
                When: "hasWorkspace && hasDesignerDocument",
                Priority: 60)));

        context.Subscriptions.Add(context.Contributions.RegisterCommandPaletteItems(
            context.ExtensionId,
            new[]
            {
                new ExtensionCommandPaletteContribution(InsertButtonCommandId, UiStrings.ToolboxInsertButton, UiStrings.Toolbox),
                new ExtensionCommandPaletteContribution(InsertSelectedCommandId, UiStrings.ToolboxInsertSelected, UiStrings.Toolbox)
            }));

        context.Subscriptions.Add(context.Contributions.RegisterMenuItems(
            context.ExtensionId,
            new[]
            {
                new ExtensionMenuContribution(
                    InsertButtonCommandId,
                    UiStrings.InsertButton,
                    ExtensionMenuLocations.Tools,
                    "toolbox",
                    10),
                new ExtensionMenuContribution(
                    InsertSelectedCommandId,
                    UiStrings.InsertSelectedToolboxItem,
                    ExtensionMenuLocations.Tools,
                    "toolbox",
                    20),
                new ExtensionMenuContribution(
                    ToggleViewCommandId,
                    UiStrings.Toolbox,
                    ExtensionMenuLocations.View,
                    "views.left",
                    20)
            }));

        IStatusBarItem loadStatus = context.Window.CreateStatusBarItem(StatusBarAlignment.Left, 30);
        loadStatus.CommandId = ShowTaskCommandId;
        context.Subscriptions.Add(loadStatus);
        context.Subscriptions.Add(viewModel.WhenAnyValue(x => x.HasLoadTask, x => x.HasLoadError, x => x.StatusText)
            .Subscribe(state =>
            {
                loadStatus.Text = UiStrings.Toolbox + ": " + (state.Property2 ? UiStrings.RetryControlDiscovery : UiStrings.LoadProjectControls);
                loadStatus.Tooltip = state.Property3;
                if (state.Property1) loadStatus.Show();
                else loadStatus.Hide();
            }));

        if (context.ControlCatalog is { HasProjectAssemblies: true })
        {
            await viewModel.ReloadAsync(cancellationToken, includeProjectControls: true);
        }
    }

    private static async Task InsertButtonAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        if (!HasInsertTarget(context)) return;
        string? insertedNodeId = await context.Designer.InsertElementAsync(
            "Button",
            "https://github.com/avaloniaui",
            parentNodeId: null,
            cancellationToken);

        if (insertedNodeId is null && HasInsertTarget(context))
        {
            await context.Window.ShowWarningMessageAsync(
                UiStrings.DesignerDocumentRequired,
                cancellationToken);
        }
    }

    private static async Task InsertSelectedAsync(
        ExtensionContext context,
        CommandContext commandContext,
        CancellationToken cancellationToken)
    {
        if (!HasInsertTarget(context)) return;
        if (!TryGetStringArg(commandContext.Arguments, 0, out string? typeName)
            || !TryGetStringArg(commandContext.Arguments, 1, out string? xmlNamespace))
        {
            await context.Window.ShowWarningMessageAsync(
                UiStrings.ToolboxArgumentsRequired,
                cancellationToken);
            return;
        }

        string? parentNodeId = null;
        _ = TryGetStringArg(commandContext.Arguments, 2, out parentNodeId);

        string? insertedNodeId = await context.Designer.InsertElementAsync(
            typeName!,
            xmlNamespace!,
            parentNodeId,
            cancellationToken);

        if (insertedNodeId is null && HasInsertTarget(context))
        {
            await context.Window.ShowWarningMessageAsync(
                UiStrings.ControlInsertFailed,
                cancellationToken);
        }
    }

    private static bool HasInsertTarget(ExtensionContext context) =>
        !string.IsNullOrWhiteSpace(context.WorkspaceInfo.WorkspacePath)
        && !string.IsNullOrWhiteSpace(context.Designer.ActiveDocumentPath);

    private static bool TryGetStringArg(IReadOnlyList<object?>? arguments, int index, out string? value)
    {
        value = null;
        if (arguments is null || index < 0 || index >= arguments.Count)
        {
            return false;
        }

        if (arguments[index] is not string text || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private sealed class ToolboxPanelViewProvider : ICustomViewProvider
    {
        private readonly ToolboxPanelViewModel _viewModel;

        public ToolboxPanelViewProvider(ToolboxPanelViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public object? CreateViewModel()
        {
            return _viewModel;
        }
    }
}
