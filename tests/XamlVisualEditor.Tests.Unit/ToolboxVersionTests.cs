using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.ToolboxExtension;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ToolboxVersionTests
{
    [AvaloniaFact]
    public async Task OfficialLibrariesStayBeforeFavoritesAndAvaloniaNamedThirdPartyLibraries()
    {
        string thirdPartyPath = ControlCatalogTests.CompileLibrary("Avalonia.Aardvark", "1.2.3.0", """
            namespace ThirdParty;
            public sealed class ThirdPartyButton : Avalonia.Controls.Button { }
            """, officialKey: false, referenceAvalonia: true);
        MetadataControlCatalog catalog = new(TypeMetadataService.ReadControlTypes);
        catalog.SetWorkspaceAssemblies(new[] { thirdPartyPath });
        SynchronizationContext context = Assert.IsAssignableFrom<SynchronizationContext>(SynchronizationContext.Current);
        using ToolboxPanelViewModel model = new(new CommandRegistry(), new InMemorySettingsStore(), catalog,
            new ReactiveUI.Primitives.Concurrency.SynchronizationContextSequencer(context));
        await model.ReloadAsync();

        ToolboxAssemblyGroup official = model.AssemblyGroups[0];
        Assert.Equal("Avalonia.Controls", official.AssemblyName);
        Assert.True(official.IsStandardAssembly);
        Assert.Equal(typeof(Control).Assembly.GetName().Version!.ToString(), official.AssemblyVersion);
        ToolboxAssemblyGroup thirdParty = Assert.Single(model.AssemblyGroups, group => group.AssemblyName == "Avalonia.Aardvark");
        Assert.False(thirdParty.IsStandardAssembly);
        Assert.All(thirdParty.Items, item => Assert.False(item.IsStandardControl));
        thirdParty.IsFavorite = true;
        Assert.True(thirdParty.IsFavorite);
        Assert.Same(official, model.AssemblyGroups[0]);
        Assert.Equal("Avalonia.Controls", model.Libraries[1].Value);
        Assert.Same(official, model.AssemblyChoices[0]);
        Assert.Contains("1.2.3.0", thirdParty.Description);
        model.SearchText = "ThirdPartyButton";
        Assert.Same(thirdParty, Assert.Single(model.AssemblyGroups));
        model.SearchText = string.Empty;
        Assert.Same(official, model.AssemblyGroups[0]);
    }
}
