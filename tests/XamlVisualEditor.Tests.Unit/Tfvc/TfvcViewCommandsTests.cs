using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.TfvcExtension.Localization;
using Xunit;
using TfvcExtensionEntry = XamlVisualEditor.TfvcExtension.TfvcExtension;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

public sealed class TfvcViewCommandsTests
{
    private const string EXTENSION_ID = "tfvc.test";

    public static TheoryData<string, string, string, string, string> ViewCommandContributions => new()
    {
        {
            TfvcExtensionEntry.TOGGLE_PENDING_CHANGES_COMMAND_ID,
            "views.right",
            TfvcStrings.PendingChangesTitle,
            TfvcStrings.TogglePendingChangesCommand,
            TfvcStrings.TogglePendingChanges
        },
        {
            TfvcExtensionEntry.TOGGLE_SOURCE_CONTROL_EXPLORER_COMMAND_ID,
            "views.left",
            TfvcStrings.SourceControlExplorerTitle,
            TfvcStrings.ToggleSourceControlExplorerCommand,
            TfvcStrings.ToggleSourceControlExplorer
        },
        {
            TfvcExtensionEntry.TOGGLE_HISTORY_COMMAND_ID,
            "views.bottom",
            TfvcStrings.HistoryTitle,
            TfvcStrings.ToggleHistoryCommand,
            TfvcStrings.ToggleHistory
        }
    };

    public static TheoryData<string, string> ViewCommandTargets => new()
    {
        { TfvcExtensionEntry.TOGGLE_PENDING_CHANGES_COMMAND_ID, TfvcExtensionEntry.PENDING_CHANGES_VIEW_ID },
        { TfvcExtensionEntry.TOGGLE_SOURCE_CONTROL_EXPLORER_COMMAND_ID, TfvcExtensionEntry.SOURCE_CONTROL_EXPLORER_VIEW_ID },
        { TfvcExtensionEntry.TOGGLE_HISTORY_COMMAND_ID, TfvcExtensionEntry.HISTORY_VIEW_ID }
    };

    [Theory]
    [MemberData(nameof(ViewCommandContributions))]
    public async Task ViewCommand_IsRegisteredWithMetadataPaletteItemAndViewMenuEntry(
        string commandId,
        string menuGroup,
        string menuTitle,
        string metadataTitle,
        string paletteTitle)
    {
        Registration registration = Register();

        IReadOnlyList<string> commandIds = await registration.Commands.GetCommandsAsync(CancellationToken.None);
        Assert.Contains(commandId, commandIds);

        Assert.True(registration.Metadata.TryGet(commandId, out CommandMetadata metadata));
        Assert.Equal(metadataTitle, metadata.Title);
        Assert.Equal("TFVC", metadata.Category);
        Assert.Null(metadata.When);

        ExtensionCommandPaletteContribution paletteItem = Assert.Single(
            registration.Contributions.CommandPaletteItems,
            item => item.CommandId == commandId);
        Assert.Equal(paletteTitle, paletteItem.Title);
        Assert.Equal("TFVC", paletteItem.Category);

        ExtensionMenuContribution menuItem = Assert.Single(
            registration.Contributions.MenuItems,
            item => item.CommandId == commandId);
        Assert.Equal(ExtensionMenuLocations.View, menuItem.Location);
        Assert.Equal(menuGroup, menuItem.Group);
        Assert.Equal(menuTitle, menuItem.Title);
        Assert.Null(menuItem.Submenu);
        Assert.Null(menuItem.CheckedWhen);
    }

    [Theory]
    [MemberData(nameof(ViewCommandTargets))]
    public async Task ViewCommand_TogglesItsView(string commandId, string viewId)
    {
        Registration registration = Register();

        await registration.Commands.ExecuteAsync(commandId, null, CancellationToken.None);

        Assert.Equal([viewId], registration.ViewHost.Toggled);
        Assert.Empty(registration.ViewHost.Shown);
    }

    [Fact]
    public void OfflineCommands_KeepTheirTfvcSubmenuAndPaletteEntriesNextToThePanelEntries()
    {
        Registration registration = Register();

        ExtensionMenuContribution workOffline = Assert.Single(
            registration.Contributions.MenuItems,
            item => item.CommandId == TfvcExtensionEntry.WORK_OFFLINE_COMMAND_ID);
        Assert.Equal(ExtensionMenuLocations.Extensions, workOffline.Location);
        Assert.Equal("TFVC", workOffline.Submenu);
        Assert.Equal(TfvcExtensionEntry.OFFLINE_CONTEXT_KEY, workOffline.CheckedWhen);
        ExtensionMenuContribution goOnline = Assert.Single(
            registration.Contributions.MenuItems,
            item => item.CommandId == TfvcExtensionEntry.GO_ONLINE_COMMAND_ID);
        Assert.Equal(ExtensionMenuLocations.Extensions, goOnline.Location);
        Assert.Equal("TFVC", goOnline.Submenu);

        string[] expectedPaletteIds =
        [
            TfvcExtensionEntry.TOGGLE_PENDING_CHANGES_COMMAND_ID,
            TfvcExtensionEntry.TOGGLE_SOURCE_CONTROL_EXPLORER_COMMAND_ID,
            TfvcExtensionEntry.TOGGLE_HISTORY_COMMAND_ID,
            TfvcExtensionEntry.WORK_OFFLINE_COMMAND_ID,
            TfvcExtensionEntry.GO_ONLINE_COMMAND_ID
        ];
        Assert.Equal(
            expectedPaletteIds.Order(StringComparer.Ordinal),
            registration.Contributions.CommandPaletteItems.Select(item => item.CommandId).Order(StringComparer.Ordinal));
        Assert.Equal(5, registration.Contributions.MenuItems.Count);
    }

    [Fact]
    public async Task DisposingTheSubscriptions_RemovesTheViewCommandsAndContributions()
    {
        Registration registration = Register();

        foreach (IDisposable subscription in registration.Subscriptions)
        {
            subscription.Dispose();
        }

        string[] viewCommandIds =
        [
            TfvcExtensionEntry.TOGGLE_PENDING_CHANGES_COMMAND_ID,
            TfvcExtensionEntry.TOGGLE_SOURCE_CONTROL_EXPLORER_COMMAND_ID,
            TfvcExtensionEntry.TOGGLE_HISTORY_COMMAND_ID
        ];
        IReadOnlyList<string> commandIds = await registration.Commands.GetCommandsAsync(CancellationToken.None);
        Assert.Empty(commandIds.Intersect(viewCommandIds));
        Assert.Empty(registration.Metadata.GetAll().Keys.Intersect(viewCommandIds));
        Assert.Empty(registration.Contributions.MenuItems);
        Assert.Empty(registration.Contributions.CommandPaletteItems);
    }

    private static Registration Register()
    {
        Registration registration = new();
        TfvcExtensionEntry.RegisterViewCommands(
            registration.Commands,
            registration.Metadata,
            registration.ViewHost,
            registration.Subscriptions);
        TfvcExtensionEntry.RegisterMenuAndPaletteItems(EXTENSION_ID, registration.Contributions, registration.Subscriptions);
        return registration;
    }

    private sealed class Registration
    {
        public CommandRegistry Commands { get; } = new();

        public CommandMetadataRegistry Metadata { get; } = new();

        public ExtensionContributionRegistry Contributions { get; } = new();

        public RecordingExtensionViewHost ViewHost { get; } = new();

        public List<IDisposable> Subscriptions { get; } = new();
    }
}
