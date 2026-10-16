using XamlVisualEditor.Core;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class EditorPreviewThemeDefaultsTests
{
    private const string PaletteId = "example.palette-themes/palette";
    private static readonly DesignerThemeProviderRegistration Palette =
        new(PaletteId, "Palette", "Palette.dll", "Example.PaletteProvider", "Example.Palette");
    private static readonly DesignerThemeProviderRegistration Undeclared =
        new("example.other-themes/other", "Other", "Other.dll", "Example.OtherProvider");

    private static ProjectModel Project(params string[] references) => new()
    {
        Name = "Sample",
        ProjectPath = Path.Combine(Path.GetTempPath(), "Sample", "Sample.csproj"),
        XamlFiles = Array.Empty<XamlFileModel>(),
        Files = Array.Empty<ProjectFileModel>(),
        References = references.Select(name => new AssemblyReference { Name = name, Path = name + ".dll" }).ToArray()
    };

    [Fact]
    public void EditorThemeOfTheSameProviderIsUsedForAReferencingProject()
    {
        DesignerThemeSelection? selection = EditorPreviewThemeDefaults.Resolve(
            new EditorThemeSelection(PaletteId, "corporate"), false, new[] { Undeclared, Palette }, Project("example.palette"));

        Assert.Equal(new DesignerThemeSelection(PaletteId, "corporate"), selection);
    }

    [Theory]
    [InlineData("light", false, "light")]
    [InlineData("dark", false, "dark")]
    [InlineData("system", true, "dark")]
    [InlineData("system", false, "light")]
    public void BuiltinEditorThemeMapsToTheThemeOfTheSameName(string editorThemeId, bool editorIsDark, string expectedThemeId)
    {
        DesignerThemeSelection? selection = EditorPreviewThemeDefaults.Resolve(
            new EditorThemeSelection(EditorPreviewThemeDefaults.BuiltinProviderId, editorThemeId), editorIsDark,
            new[] { Palette }, Project("Example.Palette"));

        Assert.Equal(new DesignerThemeSelection(PaletteId, expectedThemeId), selection);
    }

    [Fact]
    public void ProjectWithoutTheRequiredAssemblyKeepsItsDefault()
    {
        Assert.Null(EditorPreviewThemeDefaults.Resolve(new EditorThemeSelection(PaletteId, "corporate"), false,
            new[] { Palette }, Project("Other.Library")));
        Assert.Null(EditorPreviewThemeDefaults.Resolve(new EditorThemeSelection(PaletteId, "corporate"), false,
            new[] { Palette }, null));
    }

    [Fact]
    public void ProviderWithoutRequiredAssemblyNeverFollowsTheEditorTheme()
    {
        Assert.Null(EditorPreviewThemeDefaults.Resolve(new EditorThemeSelection(Undeclared.Id, "other"), false,
            new[] { Undeclared }, Project("Example.Palette")));
        Assert.Null(EditorPreviewThemeDefaults.Resolve(new EditorThemeSelection(EditorPreviewThemeDefaults.BuiltinProviderId, "dark"),
            true, new[] { Undeclared }, Project("Example.Palette")));
    }

    [Fact]
    public void ForeignEditorProviderKeepsTheProjectDefault()
    {
        Assert.Null(EditorPreviewThemeDefaults.Resolve(new EditorThemeSelection("example.editor-only/colors", "night"), true,
            new[] { Palette }, Project("Example.Palette")));
    }

    [Fact]
    public void DerivedThemeTheReportedCatalogDoesNotOfferKeepsTheProjectDefault()
    {
        DesignerTheme light = new("light", "Light", false);
        DesignerTheme corporate = new("corporate", "Corporate", false);
        DesignerThemeCatalog Catalog(string providerId, DesignerTheme[] themes, string? error = null) =>
            new(Guid.NewGuid(), new[] { new DesignerThemeProviderCatalog(providerId, "Palette", themes, error) });
        DesignerThemeSelection? ResolveDark(DesignerThemeCatalog? catalog) => EditorPreviewThemeDefaults.Resolve(
            new EditorThemeSelection(EditorPreviewThemeDefaults.BuiltinProviderId, "dark"), true,
            new[] { Palette }, Project("Example.Palette"), catalog);
        DesignerThemeCatalog withoutDark = Catalog(PaletteId, new[] { light, corporate });

        Assert.Null(ResolveDark(withoutDark));
        Assert.Null(ResolveDark(Catalog(PaletteId, Array.Empty<DesignerTheme>(), "Palette failed.")));
        Assert.Equal(new DesignerThemeSelection(PaletteId, "dark"),
            ResolveDark(Catalog(PaletteId, new[] { light, new DesignerTheme("dark", "Dark", true) })));
        Assert.Equal(new DesignerThemeSelection(PaletteId, "dark"),
            ResolveDark(Catalog(Undeclared.Id, new[] { new DesignerTheme("dark", "Dark", true) })));
        Assert.Equal(new DesignerThemeSelection(PaletteId, "dark"), ResolveDark(null));

        Assert.Null(EditorPreviewThemeDefaults.Resolve(new EditorThemeSelection(PaletteId, "forest"), false,
            new[] { Palette }, Project("Example.Palette"), withoutDark));
        Assert.Equal(new DesignerThemeSelection(PaletteId, "corporate"), EditorPreviewThemeDefaults.Resolve(
            new EditorThemeSelection(PaletteId, "corporate"), false, new[] { Palette }, Project("Example.Palette"), withoutDark));
    }
}
