using Avalonia.Headless.XUnit;
using XamlVisualEditor.Acp;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Extensions.Hosting.IdeBridge;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using Xunit;
using AcpExtensionEntry = XamlVisualEditor.AcpExtension.AcpExtension;
using AnimationEditorExtensionEntry = XamlVisualEditor.AnimationEditorExtension.AnimationEditorExtension;
using CollaborationExtensionEntry = XamlVisualEditor.CollaborationExtension.CollaborationExtension;
using DapDebuggingExtensionEntry = XamlVisualEditor.Debugging.DapExtension.DapDebuggingExtension;
using DebugSettingsExtensionEntry = XamlVisualEditor.DebugSettingsExtension.DebugSettingsExtension;
using DotNetSdkDebuggingExtensionEntry = XamlVisualEditor.Debugging.DotNetSdkExtension.DotNetSdkDebuggingExtension;
using DotNetTemplatesExtensionEntry = XamlVisualEditor.DotNetTemplatesExtension.DotNetTemplatesExtension;
using FileExplorerExtensionEntry = XamlVisualEditor.FileExplorerExtension.FileExplorerExtension;
using GitExtensionEntry = XamlVisualEditor.GitExtension.GitExtension;
using IdeBridgeExtensionEntry = XamlVisualEditor.IdeBridgeExtension.IdeBridgeExtension;
using LspSettingsExtensionEntry = XamlVisualEditor.LspSettingsExtension.LspSettingsExtension;
using McpExtensionEntry = XamlVisualEditor.McpExtension.McpExtension;
using NavigationExtensionEntry = XamlVisualEditor.NavigationExtension.NavigationExtension;
using OutputExtensionEntry = XamlVisualEditor.OutputExtension.OutputExtension;
using PropertyEditorExtensionEntry = XamlVisualEditor.PropertyEditorExtension.PropertyEditorExtension;
using SolutionExplorerExtensionEntry = XamlVisualEditor.SolutionExplorerExtension.SolutionExplorerExtension;
using TextEditingExtensionEntry = XamlVisualEditor.TextEditingExtension.TextEditingExtension;
using TfvcExtensionEntry = XamlVisualEditor.TfvcExtension.TfvcExtension;
using ToolboxExtensionEntry = XamlVisualEditor.ToolboxExtension.ToolboxExtension;
using TreeInspectorExtensionEntry = XamlVisualEditor.TreeInspectorExtension.TreeInspectorExtension;
using VscodeCompatExtensionEntry = XamlVisualEditor.VscodeCompatExtension.VscodeCompatExtension;
using WorkspaceExtensionEntry = XamlVisualEditor.WorkspaceExtension.WorkspaceExtension;
using XamlEditorExtensionEntry = XamlVisualEditor.XamlEditorExtension.XamlEditorExtension;

namespace XamlVisualEditor.Tests.UI;

public sealed class CommandPaletteCoverageTests
{
    [AvaloniaFact]
    public async Task Every_command_of_the_built_in_extensions_has_a_title_and_appears_in_the_command_palette()
    {
        using ExtensionCommandMenuIntegrationTests.ExtensionMenuTestHost host = new(builtInExtensions: CreateBuiltInExtensions);
        // The bridge serves its clients over the standard input and output by default, which belong to the test runner.
        await host.Settings.UpdateAsync("ideBridge", new IdeBridgeSettings { Enabled = false }, SettingsTarget.User,
            CancellationToken.None);
        await host.ActivateAsync();

        IReadOnlyList<string> commandIds = await host.Commands.GetCommandsAsync(CancellationToken.None);
        HashSet<string> paletteIds = host.ViewModel.CommandPaletteItems.Select(item => item.CommandId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> missing = [];
        foreach (string commandId in commandIds)
        {
            if (!host.CommandMetadata.TryGet(commandId, out CommandMetadata metadata) || string.IsNullOrWhiteSpace(metadata.Title))
                missing.Add(commandId + ": no command metadata with a title");
            if (!paletteIds.Contains(commandId))
                missing.Add(commandId + ": not in the command palette");
        }

        Assert.NotEmpty(commandIds);
        Assert.Contains("textEditing.options", commandIds);
        Assert.Empty(missing);
    }

    /// <summary>The built-in extensions in the order of the application's service registration (App.axaml.cs).</summary>
    private static IXveExtension[] CreateBuiltInExtensions(ExtensionCommandMenuIntegrationTests.ExtensionMenuTestHost host) =>
    [
        new AcpExtensionEntry(new AcpService(new AcpAgentHostFactory()), new EmptyProfileStore(), new EmptySecretStore(),
            new AcpOAuthDeviceFlowService(), host.WorkspaceInfo),
        new GitExtensionEntry(new GitService(), host.WorkspaceInfo, new InMemoryDialogHost()),
        new TfvcExtensionEntry(),
        new IdeBridgeExtensionEntry(),
        new McpExtensionEntry(),
        new VscodeCompatExtensionEntry(),
        new DotNetTemplatesExtensionEntry(new DotNetTemplateService(new DotNetCliRunner())),
        new FileExplorerExtensionEntry(host.WorkspaceInfo, host.Editor, new InMemoryFolderPicker(), host.Settings),
        new SolutionExplorerExtensionEntry(new SolutionExplorerPanelHostAdapter(host.ViewModel)),
        new ToolboxExtensionEntry(),
        new PropertyEditorExtensionEntry(),
        new OutputExtensionEntry(),
        new NavigationExtensionEntry(),
        new XamlEditorExtensionEntry(),
        new TreeInspectorExtensionEntry(),
        new AnimationEditorExtensionEntry(),
        new CollaborationExtensionEntry(),
        new DebugSettingsExtensionEntry(),
        new LspSettingsExtensionEntry(),
        new WorkspaceExtensionEntry(host.ViewModel, host.WorkspaceInfo),
        new TextEditingExtensionEntry(),
        new DapDebuggingExtensionEntry(),
        new DotNetSdkDebuggingExtensionEntry()
    ];

    private sealed class EmptyProfileStore : IAcpProfileStore
    {
        public Task<IReadOnlyList<AcpProfile>> LoadAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AcpProfile>>(Array.Empty<AcpProfile>());

        public Task SaveAsync(IReadOnlyList<AcpProfile> profiles, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public Task<string?> GetSecretAsync(string key, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task SetSecretAsync(string key, string secret, CancellationToken ct) => Task.CompletedTask;

        public Task RemoveSecretAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }
}
