using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using XamlVisualEditor.Core;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class TypeMetadataCatalogTests
{
    [Fact]
    public void ResolvingATypeFromARegisteredAssemblyKeepsTheTypeCatalog()
    {
        Type type = typeof(NullLogger);
        Assembly assembly = type.Assembly;
        string assemblyName = assembly.GetName().Name!;
        TypeMetadataService service = new();
        service.LoadAssembly(assembly.Location);
        IReadOnlyList<TypeMetadata> catalog = service.GetAvailableTypes();

        Type? resolved = service.ResolveClrType(new TypeMetadata
        {
            FullName = type.FullName!,
            Name = type.Name,
            XmlNamespace = $"clr-namespace:{type.Namespace};assembly={assemblyName}",
            ClrNamespace = type.Namespace!,
            AssemblyName = assemblyName
        });

        Assert.Same(type, resolved);
        Assert.Same(catalog, service.GetAvailableTypes());
    }
}
