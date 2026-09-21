using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.LspSettingsExtension.Views;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.LspSettingsExtension;

public sealed class LspSettingsExtension : IXveExtension
{
    private const string LspSettingsViewId = "lspSettings.panel";
    private const string ToggleLspSettingsCommandId = "lspSettings.toggleView";
    private const string LspSettingsSection = "lsp.settings.servers";

    public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        LspSettingsPanelViewModel panelViewModel = new(context.LspSettings);
        context.Subscriptions.Add(panelViewModel);

        context.Subscriptions.Add(context.Settings.RegisterSchema(new SettingsSectionSchema(
            LspSettingsSection,
            UiStrings.LspServerSettings,
            UiStrings.LspSettingsDescription,
            "array",
            ValidateLspSettings)));

        IReadOnlyList<LspServerSettings>? persisted = context.Settings.Get<IReadOnlyList<LspServerSettings>>(LspSettingsSection);
        if (persisted is { Count: > 0 })
        {
            _ = context.LspSettings.SaveServersAsync(persisted, CancellationToken.None);
        }

        context.Subscriptions.Add(context.Settings.SubscribeSection<IReadOnlyList<LspServerSettings>>(
            LspSettingsSection,
            args =>
            {
                IReadOnlyList<LspServerSettings>? servers = args.Value;
                if (servers is null)
                {
                    return;
                }

                _ = context.LspSettings.SaveServersAsync(servers, CancellationToken.None);
            }));

        EventHandler<LspSettingsChangedEventArgs> hostChanged = (_, args) =>
            _ = context.Settings.UpdateAsync(
                LspSettingsSection,
                args.Servers,
                SettingsTarget.User,
                CancellationToken.None);
        context.LspSettings.Changed += hostChanged;
        context.Subscriptions.Add(System.Reactive.Disposables.Disposable.Create(() =>
            context.LspSettings.Changed -= hostChanged));

        context.Subscriptions.Add(context.Commands.Register(
            ToggleLspSettingsCommandId,
            _ => context.ViewHost.ToggleAsync(LspSettingsViewId, CancellationToken.None)));

        context.Subscriptions.Add(context.CommandMetadata.Register(
            ToggleLspSettingsCommandId,
            new CommandMetadata(
                Title: UiStrings.ToggleLspSettingsCommand,
                Category: UiStrings.Settings,
                Priority: 90)));

        context.Subscriptions.Add(context.Contributions.RegisterViews(
            context.ExtensionId,
            new[]
            {
                new ExtensionViewContribution(
                    LspSettingsViewId,
                    UiStrings.LspSettings,
                    ExtensionViewType.Custom,
                    ExtensionViewLocation.Bottom,
                    70,
                    ActivateByDefault: true)
            }));

        context.Subscriptions.Add(context.Contributions.RegisterMenuItems(
            context.ExtensionId,
            new[]
            {
                new ExtensionMenuContribution(
                    ToggleLspSettingsCommandId,
                    UiStrings.LspSettings,
                    ExtensionMenuLocations.View,
                    "views.bottom",
                    90)
            }));

        context.Subscriptions.Add(context.Contributions.RegisterCommandPaletteItems(
            context.ExtensionId,
            new[]
            {
                new ExtensionCommandPaletteContribution(
                    ToggleLspSettingsCommandId,
                    UiStrings.ToggleLspSettings,
                    UiStrings.Settings)
            }));

        context.Subscriptions.Add(context.Views.RegisterCustomViewProvider(
            LspSettingsViewId,
            new LspSettingsViewProvider(panelViewModel)));

        return Task.CompletedTask;
    }

    private sealed class LspSettingsViewProvider : ICustomViewProvider
    {
        private readonly LspSettingsPanelViewModel _viewModel;

        public LspSettingsViewProvider(LspSettingsPanelViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public object? CreateViewModel()
        {
            return _viewModel;
        }
    }

    private static IReadOnlyList<SettingsValidationIssue> ValidateLspSettings(object? value)
    {
        if (value is null)
        {
            return Array.Empty<SettingsValidationIssue>();
        }

        if (value is not IReadOnlyList<LspServerSettings> servers)
        {
            return new[]
            {
                new SettingsValidationIssue(UiStrings.LspSettingsListExpected)
            };
        }

        foreach (LspServerSettings server in servers)
        {
            if (string.IsNullOrWhiteSpace(server.LanguageId))
            {
                return new[]
                {
                    new SettingsValidationIssue(UiStrings.LanguageIdRequired, "LanguageId")
                };
            }

            if (string.IsNullOrWhiteSpace(server.ServerPath))
            {
                return new[]
                {
                    new SettingsValidationIssue(UiStrings.ServerPathRequired, "ServerPath")
                };
            }
        }

        return Array.Empty<SettingsValidationIssue>();
    }
}
