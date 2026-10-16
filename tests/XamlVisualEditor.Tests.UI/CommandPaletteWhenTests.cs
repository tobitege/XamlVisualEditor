using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class CommandPaletteWhenTests
{
    private const string COMMAND_ID = "paletteProbe.run";
    private const string CONTEXT_KEY = "paletteProbe.enabled";

    [AvaloniaFact]
    public async Task The_palette_offers_and_runs_a_command_only_while_its_when_clause_holds()
    {
        PaletteProbeExtension probe = new();
        using ExtensionCommandMenuIntegrationTests.ExtensionMenuTestHost host = new(additionalExtension: probe);
        await host.ActivateAsync();
        List<string> offered = [];
        Action? beforeSelection = null;
        using IDisposable handler = host.ViewModel.CommandPaletteInteraction.RegisterHandler(interaction =>
        {
            offered = interaction.Input.Items.Select(item => item.CommandId).ToList();
            beforeSelection?.Invoke();
            interaction.SetOutput(interaction.Input.Items.FirstOrDefault(item => item.CommandId == COMMAND_ID));
        });
        host.ContextKeys.Set(CONTEXT_KEY, false);

        await OpenPaletteAsync(host);

        Assert.Contains(host.ViewModel.CommandPaletteItems, item => item.CommandId == COMMAND_ID);
        Assert.DoesNotContain(COMMAND_ID, offered);
        Assert.Equal(0, probe.Runs);

        host.ContextKeys.Set(CONTEXT_KEY, true);
        await OpenPaletteAsync(host);

        Assert.Contains(COMMAND_ID, offered);
        Assert.Equal(1, probe.Runs);

        // The context key turns false while the palette is open, so the selected item is stale.
        beforeSelection = () => host.ContextKeys.Set(CONTEXT_KEY, false);
        await OpenPaletteAsync(host);

        Assert.Contains(COMMAND_ID, offered);
        Assert.Equal(1, probe.Runs);
        Assert.Equal(string.Format(XamlVisualEditor.Core.Localization.UiStrings.ExtensionCommandUnavailable, COMMAND_ID),
            host.ViewModel.StatusText);
    }

    private static async Task OpenPaletteAsync(ExtensionCommandMenuIntegrationTests.ExtensionMenuTestHost host)
    {
        await Dispatcher.UIThread.InvokeAsync(
            () => host.ViewModel.ShowCommandPaletteCommand.Execute().ToTask(),
            DispatcherPriority.Background);
        await host.FlushAsync();
    }

    private sealed class PaletteProbeExtension : IXveExtension
    {
        public int Runs { get; private set; }

        public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
        {
            context.Subscriptions.Add(context.Commands.Register(COMMAND_ID, _ =>
            {
                Runs++;
                return Task.CompletedTask;
            }));
            context.Subscriptions.Add(context.CommandMetadata.Register(
                COMMAND_ID,
                new CommandMetadata("Run Palette Probe", "Probe", When: CONTEXT_KEY)));
            context.Subscriptions.Add(context.Contributions.RegisterCommandPaletteItems(
                context.ExtensionId,
                [new ExtensionCommandPaletteContribution(COMMAND_ID, "Run Palette Probe", "Probe")]));
            return Task.CompletedTask;
        }
    }
}
