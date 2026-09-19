using Avalonia.Controls;
using XamlVisualEditor.Core;
using XamlVisualEditor.TestControls;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ControlCatalogIntegrationTests
{
    [Fact]
    public void LoadedLibraryProvidesConstructibleControlsContainersAndInheritedProperties()
    {
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(CaptionControl).Assembly.Location);
        string assemblyName = typeof(CaptionControl).Assembly.GetName().Name!;
        string xmlNamespace = $"clr-namespace:XamlVisualEditor.TestControls;assembly={assemblyName}";

        IReadOnlyList<TypeMetadata> catalog = metadata.GetAvailableTypes();
        TypeMetadata caption = Assert.Single(catalog, type => type.FullName == typeof(CaptionControl).FullName);
        Assert.True(caption.IsControl && caption.IsContentControl && caption.IsInstantiable);
        Assert.Equal("Library caption", caption.DisplayName);
        Assert.Equal("Library content", caption.Category);
        Assert.Contains(catalog, type => type.FullName == typeof(LibraryGrid).FullName && type.IsPanel && type.IsInstantiable);
        Assert.Contains(catalog, type => type.FullName == typeof(ClosedGenericControl).FullName && type.IsControl && type.IsInstantiable);
        Assert.Contains(catalog, type => type.FullName == typeof(ConstructorTrapControl).FullName && type.IsInstantiable);
        Assert.Contains(catalog, type => type.FullName == typeof(AbstractControl).FullName && !type.IsInstantiable);
        Assert.Contains(catalog, type => type.FullName == typeof(GenericControl<>).FullName && !type.IsInstantiable);
        Assert.Contains(catalog, type => type.FullName == typeof(ServiceControl).FullName && !type.IsInstantiable);
        Assert.Contains(catalog, type => type.FullName == typeof(HiddenControl).FullName && !type.IsToolboxVisible);
        Assert.Contains(metadata.GetAvailableTypes(xmlNamespace), type => type.FullName == caption.FullName);
        Assert.Contains(metadata.GetAvailableTypes("using:XamlVisualEditor.TestControls"), type => type.FullName == caption.FullName);

        TypeMetadata resolved = Assert.IsType<TypeMetadata>(metadata.GetType(xmlNamespace, nameof(CaptionControl)));
        Assert.Equal(typeof(CaptionControl), metadata.ResolveClrType(resolved));
        IReadOnlyList<PropertyMetadata> properties = metadata.GetProperties(resolved);
        Assert.Contains(properties, property => property.Name == nameof(CaptionControl.Caption) && property.ClrType == typeof(string));
        Assert.Contains(properties, property => property.Name == nameof(Control.Width));
    }

    [Fact]
    public void SameControlNameResolvesOnlyWithinItsDeclaredNamespace()
    {
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(CaptionControl).Assembly.Location);
        string libraryNamespace = $"clr-namespace:XamlVisualEditor.TestControls;assembly={typeof(CaptionControl).Assembly.GetName().Name}";

        TypeMetadata standard = Assert.IsType<TypeMetadata>(metadata.GetType("https://github.com/avaloniaui", "Button"));
        TypeMetadata external = Assert.IsType<TypeMetadata>(metadata.GetType(libraryNamespace, "Button"));
        TypeMetadata uppercase = Assert.IsType<TypeMetadata>(metadata.GetType(libraryNamespace, "BUTTON"));

        Assert.Equal(typeof(Avalonia.Controls.Button), metadata.ResolveClrType(standard));
        Assert.Equal(typeof(TestControls.Button), metadata.ResolveClrType(external));
        Assert.Equal(typeof(BUTTON), metadata.ResolveClrType(uppercase));
        Assert.Null(metadata.GetType("clr-namespace:Missing.Controls;assembly=Missing.Library", "Button"));
        Assert.Null(metadata.GetType("https://invalid.example/controls", "Button"));
    }
}
