using System;
using System.Linq;
using Xunit;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;

namespace XamlVisualEditor.Tests.Unit.Extensions;

public sealed class ExtensionContributionRegistryTests
{
    [Fact]
    public void RegisterMenuItems_AddsItemsAndRaisesChanged()
    {
        var registry = new ExtensionContributionRegistry();
        int changedCount = 0;
        registry.Changed += (_, _) => changedCount++;

        using (registry.RegisterMenuItems("ext", new[]
        {
            new ExtensionMenuContribution("cmd.hello", "Hello", ExtensionMenuLocations.Extensions, "group")
        }))
        {
            Assert.Single(registry.MenuItems);
        }

        Assert.Empty(registry.MenuItems);
        Assert.Equal(2, changedCount);
    }

    [Fact]
    public void RepeatedRegistrationsOfOneExtensionAreAllVisible()
    {
        var registry = new ExtensionContributionRegistry();

        using IDisposable menuOne = registry.RegisterMenuItems("ext", new[] { Menu("cmd.one") });
        using IDisposable menuTwo = registry.RegisterMenuItems("ext", new[] { Menu("cmd.two") });
        using IDisposable toolbarOne = registry.RegisterToolbarItems("ext", new[] { Toolbar("cmd.one") });
        using IDisposable toolbarTwo = registry.RegisterToolbarItems("ext", new[] { Toolbar("cmd.two") });
        using IDisposable paletteOne = registry.RegisterCommandPaletteItems("ext", new[] { Palette("cmd.one") });
        using IDisposable paletteTwo = registry.RegisterCommandPaletteItems("ext", new[] { Palette("cmd.two") });
        using IDisposable viewOne = registry.RegisterViews("ext", new[] { View("view.one") });
        using IDisposable viewTwo = registry.RegisterViews("ext", new[] { View("view.two") });
        using IDisposable editorOne = registry.RegisterPropertyEditors("ext", new[] { Editor("System.String") });
        using IDisposable editorTwo = registry.RegisterPropertyEditors("ext", new[] { Editor("System.Boolean") });

        Assert.Equal(new[] { "cmd.one", "cmd.two" }, registry.MenuItems.Select(item => item.CommandId));
        Assert.Equal(new[] { "cmd.one", "cmd.two" }, registry.ToolbarItems.Select(item => item.CommandId));
        Assert.Equal(new[] { "cmd.one", "cmd.two" }, registry.CommandPaletteItems.Select(item => item.CommandId));
        Assert.Equal(new[] { "view.one", "view.two" }, registry.ViewContributions.Select(item => item.ViewId));
        Assert.Equal(new[] { "System.String", "System.Boolean" }, registry.PropertyEditors.Select(item => item.PropertyType));
    }

    [Fact]
    public void DisposingOneRegistrationRemovesOnlyItsItems()
    {
        var registry = new ExtensionContributionRegistry();
        IDisposable first = registry.RegisterMenuItems("ext", new[] { Menu("cmd.one"), Menu("cmd.two") });
        using IDisposable second = registry.RegisterMenuItems("ext", new[] { Menu("cmd.three") });
        IDisposable firstView = registry.RegisterViews("ext", new[] { View("view.one") });
        using IDisposable secondView = registry.RegisterViews("ext", new[] { View("view.two") });

        first.Dispose();
        firstView.Dispose();

        Assert.Equal("cmd.three", Assert.Single(registry.MenuItems).CommandId);
        Assert.Equal("view.two", Assert.Single(registry.ViewContributions).ViewId);
    }

    [Fact]
    public void DisposingARegistrationTwiceIsHarmless()
    {
        var registry = new ExtensionContributionRegistry();
        IDisposable first = registry.RegisterCommandPaletteItems("ext", new[] { Palette("cmd.one") });
        using IDisposable second = registry.RegisterCommandPaletteItems("ext", new[] { Palette("cmd.two") });
        int changedCount = 0;
        registry.Changed += (_, _) => changedCount++;

        first.Dispose();
        first.Dispose();

        Assert.Equal("cmd.two", Assert.Single(registry.CommandPaletteItems).CommandId);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void ItemsListExtensionsInFirstRegistrationOrderAndRegistrationsInCallOrder()
    {
        var registry = new ExtensionContributionRegistry();

        using IDisposable alphaOne = registry.RegisterToolbarItems("alpha", new[] { Toolbar("alpha.one"), Toolbar("alpha.two") });
        using IDisposable beta = registry.RegisterToolbarItems("beta", new[] { Toolbar("beta.one") });
        using IDisposable alphaTwo = registry.RegisterToolbarItems("alpha", new[] { Toolbar("alpha.three") });

        Assert.Equal(new[] { "alpha.one", "alpha.two", "alpha.three", "beta.one" },
            registry.ToolbarItems.Select(item => item.CommandId));
    }

    [Fact]
    public void ThemeProviderIdsStayUniqueAcrossRegistrations()
    {
        var registry = new ExtensionContributionRegistry();
        string assemblyPath = typeof(ExtensionContributionRegistryTests).Assembly.Location;
        DesignerThemeProviderRegistration preview = new("ext/palette", "Palette", assemblyPath, "Sample.PreviewProvider");
        EditorThemeProviderRegistration editor = new("ext/palette", "Palette", assemblyPath, "Sample.EditorProvider");

        IDisposable previewRegistration = registry.RegisterDesignerThemeProviders("ext", new[] { preview });
        IDisposable editorRegistration = registry.RegisterEditorThemeProviders("ext", new[] { editor });

        Assert.Throws<InvalidOperationException>(() => registry.RegisterDesignerThemeProviders("other", new[] { preview }));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterEditorThemeProviders("ext", new[] { editor }));
        Assert.Equal(preview, Assert.Single(registry.DesignerThemeProviders));
        Assert.Equal(editor, Assert.Single(registry.EditorThemeProviders));

        previewRegistration.Dispose();
        editorRegistration.Dispose();
        using IDisposable previewAgain = registry.RegisterDesignerThemeProviders("ext", new[] { preview });
        using IDisposable editorAgain = registry.RegisterEditorThemeProviders("ext", new[] { editor });

        Assert.Equal(preview, Assert.Single(registry.DesignerThemeProviders));
        Assert.Equal(editor, Assert.Single(registry.EditorThemeProviders));
    }

    private static ExtensionMenuContribution Menu(string commandId) =>
        new(commandId, commandId, ExtensionMenuLocations.Extensions, "group");

    private static ExtensionToolbarContribution Toolbar(string commandId) =>
        new(commandId, commandId, null, ExtensionToolbarLocations.Extensions, "group");

    private static ExtensionCommandPaletteContribution Palette(string commandId) =>
        new(commandId, commandId, "Category");

    private static ExtensionViewContribution View(string viewId) =>
        new(viewId, viewId, ExtensionViewType.Tree, ExtensionViewLocation.Left, 0);

    private static ExtensionPropertyEditorContribution Editor(string propertyType) =>
        new(propertyType, PropertyEditorKind.Text);
}
