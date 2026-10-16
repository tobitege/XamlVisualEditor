using System.Reactive.Linq;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;

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
            workspaceInfo: context.WorkspaceInfo, storage: context.Storage, designer: context.Designer,
            editor: context.Editor);
        context.Subscriptions.Add(viewModel);
        await viewModel.InitializePreferencesAsync(cancellationToken);
        // Extensions activate one after another; the view model reports loading progress and errors itself.
        if (context.ControlCatalog is not null)
            _ = viewModel.LoadCatalogAsync(cancellationToken);

        context.Subscriptions.Add(context.Commands.Register(InsertButtonCommandId, _ => InsertButtonAsync(context, cancellationToken)));
        context.Subscriptions.Add(context.Commands.Register(InsertSelectedCommandId, commandContext =>
            InsertSelectedAsync(context, viewModel, commandContext, cancellationToken)));
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
                When: "hasDesignerDocument",
                Priority: 50)));

        context.Subscriptions.Add(context.CommandMetadata.Register(
            InsertSelectedCommandId,
            new CommandMetadata(
                Title: UiStrings.ToolboxInsertSelected,
                Category: UiStrings.Toolbox,
                When: "hasDesignerDocument",
                Priority: 60)));

        context.Subscriptions.Add(context.CommandMetadata.Register(
            ShowTaskCommandId,
            new CommandMetadata(
                Title: UiStrings.ToolboxShowLoadTask,
                Category: UiStrings.Toolbox,
                Priority: 70)));

        context.Subscriptions.Add(context.CommandMetadata.Register(
            ToggleViewCommandId,
            new CommandMetadata(
                Title: UiStrings.ToggleToolboxCommand,
                Category: UiStrings.View,
                Priority: 80)));

        context.Subscriptions.Add(context.Contributions.RegisterCommandPaletteItems(
            context.ExtensionId,
            new[]
            {
                new ExtensionCommandPaletteContribution(InsertButtonCommandId, UiStrings.ToolboxInsertButton, UiStrings.Toolbox),
                new ExtensionCommandPaletteContribution(InsertSelectedCommandId, UiStrings.ToolboxInsertSelected, UiStrings.Toolbox),
                new ExtensionCommandPaletteContribution(ShowTaskCommandId, UiStrings.ToolboxShowLoadTask, UiStrings.Toolbox),
                new ExtensionCommandPaletteContribution(ToggleViewCommandId, UiStrings.ToggleToolbox, UiStrings.View)
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
        context.Subscriptions.Add(viewModel.WhenAnyValue(x => x.HasLoadTask, x => x.HasLoadError, x => x.IsLoading,
                x => x.IsLoadingProjectControls, x => x.StatusText)
            .Subscribe(state =>
            {
                loadStatus.Text = UiStrings.Toolbox + ": " + (state.Property2 ? UiStrings.RetryControlDiscovery
                    : state.Property4 ? UiStrings.LoadingProjectControls
                    : state.Property3 ? UiStrings.LoadingStandardControls : UiStrings.LoadProjectControls);
                loadStatus.Tooltip = state.Property5;
                if (state.Property1) loadStatus.Show();
                else loadStatus.Hide();
            }));
    }

    private static async Task InsertButtonAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        if (!await EnsureInsertTargetAsync(context, cancellationToken)) return;
        string? insertedNodeId = await context.Designer.InsertElementAsync(
            "Button",
            "https://github.com/avaloniaui",
            parentNodeId: null,
            cancellationToken);

        if (insertedNodeId is null)
        {
            await context.Window.ShowWarningMessageAsync(
                UiStrings.ControlInsertFailed,
                cancellationToken);
        }
    }

    private static async Task InsertSelectedAsync(
        ExtensionContext context,
        ToolboxPanelViewModel viewModel,
        CommandContext commandContext,
        CancellationToken cancellationToken)
    {
        if (!await EnsureInsertTargetAsync(context, cancellationToken)) return;
        string? typeName;
        string? xmlNamespace;
        string? parentNodeId = null;
        if (commandContext.Arguments is not { Count: > 0 })
        {
            // Without arguments, as from the palette or the menu, the command inserts the control selected in the toolbox.
            if (viewModel.SelectedItem is not { CanDrag: true } selected)
            {
                await context.Window.ShowWarningMessageAsync(UiStrings.ToolboxSelectionRequired, cancellationToken);
                return;
            }

            typeName = selected.TypeName;
            xmlNamespace = selected.XmlNamespace;
        }
        else if (!TryGetStringArg(commandContext.Arguments, 0, out typeName)
            || !TryGetStringArg(commandContext.Arguments, 1, out xmlNamespace))
        {
            await context.Window.ShowWarningMessageAsync(
                UiStrings.ToolboxArgumentsRequired,
                cancellationToken);
            return;
        }
        else
        {
            _ = TryGetStringArg(commandContext.Arguments, 2, out parentNodeId);
        }

        string? insertedNodeId = await context.Designer.InsertElementAsync(
            typeName!,
            xmlNamespace!,
            parentNodeId,
            cancellationToken);

        if (insertedNodeId is null)
        {
            await context.Window.ShowWarningMessageAsync(
                UiStrings.ControlInsertFailed,
                cancellationToken);
        }
    }

    // Palette, menu and panel offer the commands only with an insert target; a direct call without one gets a warning.
    private static async Task<bool> EnsureInsertTargetAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        if (ToolboxPanelViewModel.IsActiveDesignerDocument(context.Designer, context.Editor)) return true;
        await context.Window.ShowWarningMessageAsync(UiStrings.DesignerDocumentRequired, cancellationToken);
        return false;
    }

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
