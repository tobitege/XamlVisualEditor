using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.DebugSettingsExtension.Views;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.DebugSettingsExtension;

public sealed class DebugSettingsExtension : IXveExtension
{
    private const string DebugSettingsViewId = "debugSettings.panel";
    private const string ToggleDebugSettingsCommandId = "debugSettings.toggleView";
    private const string DebugSettingsSection = "debug.settings";

    public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        DebugSettingsPanelViewModel panelViewModel = new(context.DebuggerRegistry, context.DebugSettings);
        context.Subscriptions.Add(panelViewModel);

        context.Subscriptions.Add(context.Settings.RegisterSchema(new SettingsSectionSchema(
            DebugSettingsSection,
            UiStrings.DebugSettings,
            UiStrings.DebugSettingsDescription,
            "object",
            ValidateDebugSettings)));

        DebugSettingsDocument? persisted = context.Settings.Get<DebugSettingsDocument>(DebugSettingsSection);
        if (persisted is not null)
        {
            _ = ApplyDebugSettingsAsync(context.DebugSettings, persisted);
        }

        context.Subscriptions.Add(context.Settings.SubscribeSection<DebugSettingsDocument>(
            DebugSettingsSection,
            args =>
            {
                if (args.Value is null)
                {
                    return;
                }

                _ = ApplyDebugSettingsAsync(context.DebugSettings, args.Value);
            }));

        EventHandler<DebugSettingsChangedEventArgs> hostChanged = (_, args) =>
        {
            DebugSettingsDocument document = new(
                args.State.AdapterPath,
                args.State.AutoDownloadTools);
            _ = context.Settings.UpdateAsync(
                DebugSettingsSection,
                document,
                SettingsTarget.User,
                CancellationToken.None);
        };
        context.DebugSettings.Changed += hostChanged;
        context.Subscriptions.Add(System.Reactive.Disposables.Disposable.Create(() =>
            context.DebugSettings.Changed -= hostChanged));

        context.Subscriptions.Add(context.Commands.Register(
            ToggleDebugSettingsCommandId,
            _ => context.ViewHost.ToggleAsync(DebugSettingsViewId, CancellationToken.None)));

        context.Subscriptions.Add(context.CommandMetadata.Register(
            ToggleDebugSettingsCommandId,
            new CommandMetadata(
                Title: UiStrings.ToggleDebugSettingsCommand,
                Category: UiStrings.Settings,
                Priority: 85)));

        context.Subscriptions.Add(context.Contributions.RegisterViews(
            context.ExtensionId,
            new[]
            {
                new ExtensionViewContribution(
                    DebugSettingsViewId,
                    UiStrings.DebugSettings,
                    ExtensionViewType.Custom,
                    ExtensionViewLocation.Bottom,
                    60,
                    ActivateByDefault: true)
            }));

        context.Subscriptions.Add(context.Contributions.RegisterMenuItems(
            context.ExtensionId,
            new[]
            {
                new ExtensionMenuContribution(
                    ToggleDebugSettingsCommandId,
                    UiStrings.DebugSettings,
                    ExtensionMenuLocations.View,
                    "views.bottom",
                    80)
            }));

        context.Subscriptions.Add(context.Contributions.RegisterCommandPaletteItems(
            context.ExtensionId,
            new[]
            {
                new ExtensionCommandPaletteContribution(
                    ToggleDebugSettingsCommandId,
                    UiStrings.ToggleDebugSettings,
                    UiStrings.Settings)
            }));

        context.Subscriptions.Add(context.Views.RegisterCustomViewProvider(
            DebugSettingsViewId,
            new DebugSettingsViewProvider(panelViewModel)));

        return Task.CompletedTask;
    }

    private sealed class DebugSettingsViewProvider : ICustomViewProvider
    {
        private readonly DebugSettingsPanelViewModel _viewModel;

        public DebugSettingsViewProvider(DebugSettingsPanelViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public object? CreateViewModel()
        {
            return _viewModel;
        }
    }

    private static IReadOnlyList<SettingsValidationIssue> ValidateDebugSettings(object? value)
    {
        if (value is null)
        {
            return Array.Empty<SettingsValidationIssue>();
        }

        if (value is not DebugSettingsDocument)
        {
            return new[]
            {
                new SettingsValidationIssue(UiStrings.DebugSettingsDocumentExpected)
            };
        }

        return Array.Empty<SettingsValidationIssue>();
    }

    private static async Task ApplyDebugSettingsAsync(IDebugSettingsHost host, DebugSettingsDocument document)
    {
        await host.SetAdapterPathAsync(document.AdapterPath, CancellationToken.None).ConfigureAwait(false);
        await host.SetAutoDownloadToolsAsync(document.AutoDownloadTools, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed record DebugSettingsDocument(string AdapterPath, bool AutoDownloadTools);
}
