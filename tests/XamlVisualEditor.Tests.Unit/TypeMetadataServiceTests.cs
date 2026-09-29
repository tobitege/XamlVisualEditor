using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlVisualEditor.Core;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class TypeMetadataServiceTests
{
    private static bool s_regularPropertyOwnerInitialized;
    [Fact]
    public void GetAvailableNamespaces_IncludesAvaloniaNamespaceAfterLoad()
    {
        TypeMetadataService service = new();
        string assemblyPath = typeof(Button).Assembly.Location;

        service.LoadAssembly(assemblyPath);

        string[] namespaces = service.GetAvailableNamespaces().ToArray();
        Assert.Contains("https://github.com/avaloniaui", namespaces);
    }

    [Fact]
    public void GetType_ResolvesFromXmlNamespaceMapping()
    {
        TypeMetadataService service = new();
        string assemblyPath = typeof(Button).Assembly.Location;
        string assemblyName = typeof(Button).Assembly.GetName().Name ?? string.Empty;

        service.LoadAssembly(assemblyPath);

        TypeMetadata? meta = service.GetType("https://github.com/avaloniaui", "Button");
        Assert.NotNull(meta);
        Assert.Equal("Button", meta!.Name);
        Assert.Equal(assemblyName, meta.AssemblyName);
    }

    [Fact]
    public void GetAvailableTypes_ReturnsControlsFromMappedNamespace()
    {
        TypeMetadataService service = new();
        string assemblyPath = typeof(Button).Assembly.Location;

        service.LoadAssembly(assemblyPath);

        var types = service.GetAvailableTypes("https://github.com/avaloniaui");
        Assert.Contains(types, t => string.Equals(t.Name, "Button", StringComparison.Ordinal));
    }

    [Fact]
    public void GetProperties_IncludesBuiltInAttachedPropertiesOnRepeatedLookups()
    {
        TypeMetadataService service = new();
        TypeMetadata type = Assert.IsType<TypeMetadata>(service.GetType("https://github.com/avaloniaui", "Button"));

        for (int lookup = 0; lookup < 2; lookup++)
        {
            IReadOnlyList<PropertyMetadata> properties = service.GetProperties(type);
            PropertyMetadata left = Assert.Single(properties, p => p.Name == "Canvas.Left");
            Assert.True(left.IsAttached);
            Assert.Equal(typeof(double), left.ClrType);
            Assert.Equal(typeof(Canvas).FullName, left.OwnerType);
            Assert.Contains(properties, p => p.Name == "Canvas.Top" && p.IsAttached);
            Assert.Contains(properties, p => p.Name == "Grid.Row" && p.IsAttached);
            Assert.Contains(properties, p => p.Name == "DockPanel.Dock" && p.IsAttached);
        }
    }

    [Fact]
    public void GetProperties_IncludesNestedNonPublicAttachedPropertyOwners()
    {
        TypeMetadataService service = new();
        service.LoadAssembly(typeof(TypeMetadataServiceTests).Assembly.Location);
        TypeMetadata type = Assert.IsType<TypeMetadata>(service.GetType("https://github.com/avaloniaui", "Button"));

        PropertyMetadata property = Assert.Single(service.GetProperties(type), p => p.Name == "NestedAttachedOwner.Hint");

        Assert.True(property.IsAttached);
        Assert.Equal(typeof(NestedAttachedOwner).FullName, property.OwnerType);
        Assert.Equal(typeof(string), property.ClrType);
        Assert.Equal("ready", property.DefaultValue);
        Assert.Equal("Test properties", property.Category);
        Assert.Equal("A custom attached hint.", property.Description);
        PropertyMetadata alias = Assert.Single(service.GetProperties(type), p => p.Name == "AliasedAttachedOwner.Note");
        Assert.True(alias.IsAttached);
        Assert.Equal("aliased", alias.DefaultValue);
        Assert.False(s_regularPropertyOwnerInitialized);
    }

    [Fact]
    public void AttachedPropertyDiscovery_DoesNotLoadUnrelatedTypesOrDependencies()
    {
        using MemoryStream dependency = CompileMetadataAssembly("MissingDependency", """
            namespace Missing { public class Base { } }
            """);
        MetadataReference dependencyReference = MetadataReference.CreateFromImage(dependency.ToArray());
        using MemoryStream assembly = CompileMetadataAssembly("PropertyDiscoveryFixture", """
            namespace Avalonia
            {
                public class AvaloniaProperty { }
                public class AttachedProperty<T> : AvaloniaProperty { }
                public class StyledProperty<T> : AvaloniaProperty { }
            }
            namespace Fixture
            {
                public class Unrelated : Missing.Base { }
                public class Owner
                {
                    public static Avalonia.AttachedProperty<int> Tag;
                    public static Avalonia.AvaloniaProperty Untyped;
                    public static Avalonia.StyledProperty<int> Styled;
                    public static volatile Avalonia.AvaloniaProperty Modified;
                    public static string OtherProperty;
                    public Avalonia.AttachedProperty<int> Instance;
                    private static Avalonia.AttachedProperty<int> Hidden;
                    private class Nested
                    {
                        public static Avalonia.AttachedProperty<int> NestedTag;
                    }
                }
                public class GenericOwner<T>
                {
                    public static Avalonia.AttachedProperty<int> GenericTag;
                    public class Nested
                    {
                        public static Avalonia.AttachedProperty<int> NestedGenericTag;
                    }
                }
            }
            """, dependencyReference);

        IReadOnlyList<AttachedPropertyField> fields = AttachedPropertyDiscovery.Read(assembly);

        Assert.Equal(5, fields.Count);
        Assert.Contains(new AttachedPropertyField("Fixture.Owner", "Tag"), fields);
        Assert.Contains(new AttachedPropertyField("Fixture.Owner", "Untyped"), fields);
        Assert.Contains(new AttachedPropertyField("Fixture.Owner", "Styled"), fields);
        Assert.Contains(new AttachedPropertyField("Fixture.Owner", "Modified"), fields);
        Assert.Contains(new AttachedPropertyField("Fixture.Owner+Nested", "NestedTag"), fields);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
            assembly => assembly.GetName().Name is "MissingDependency" or "PropertyDiscoveryFixture");
    }

    [Theory]
    [InlineData("https://github.com/avaloniaui")]
    [InlineData("clr-namespace:Avalonia.Controls;assembly=Avalonia.Controls")]
    public void RepeatedTypeLookups_DoNotReallocateNamespaceCatalogs(string xmlNamespace)
    {
        TypeMetadataService service = new();
        TypeMetadata expected = Assert.IsType<TypeMetadata>(service.GetType(xmlNamespace, "Button"));
        Assert.Same(expected, service.GetType(xmlNamespace, "Avalonia.Controls.Button"));
        int expectedCount = service.GetAvailableTypes(xmlNamespace).Count;
        TypeMetadata? actual = null;
        int actualCount = 0;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
        {
            actualCount = service.GetAvailableTypes(xmlNamespace).Count;
            actual = service.GetType(xmlNamespace, "Button");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(expected, actual);
        Assert.Equal(expectedCount, actualCount);
        Assert.True(allocated < 4096, $"Repeated metadata lookups allocated {allocated} bytes.");
    }

    [Fact]
    public void LoadAssembly_RefreshesPreviouslyQueriedNamespace()
    {
        TypeMetadataService service = new();
        string assemblyName = typeof(Console).Assembly.GetName().Name!;
        string xmlNamespace = $"clr-namespace:System;assembly={assemblyName}";
        Assert.Empty(service.GetAvailableTypes(xmlNamespace));

        service.LoadAssembly(typeof(Console).Assembly.Location);

        TypeMetadata type = Assert.Single(service.GetAvailableTypes(xmlNamespace), item => item.Name == "Console");
        Assert.Equal(assemblyName, type.AssemblyName);
        Assert.Same(type, service.GetType(xmlNamespace, "System.Console"));
        Assert.Same(type, service.GetTypeMetadata("System.Console"));
    }

    private static MemoryStream CompileMetadataAssembly(string name, string source, params MetadataReference[] references)
    {
        List<MetadataReference> allReferences = new(references)
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
        };
        CSharpCompilation compilation = CSharpCompilation.Create(name,
            new[] { CSharpSyntaxTree.ParseText(source) }, allReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        stream.Position = 0;
        return stream;
    }

    private sealed class RegularPropertyOwner : AvaloniaObject
    {
        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<RegularPropertyOwner, string>("Text", string.Empty);

        static RegularPropertyOwner()
        {
            s_regularPropertyOwnerInitialized = true;
            throw new InvalidOperationException("Unrelated styled-property owners must not be initialized by attached-property discovery.");
        }

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }
    }

    private static class AliasedAttachedOwner
    {
        public static readonly StyledProperty<string> NoteProperty =
            AvaloniaProperty.RegisterAttached<AvaloniaObject, string>("Note", typeof(AliasedAttachedOwner), "aliased");

        public static string GetNote(AvaloniaObject target) => target.GetValue(NoteProperty);
        public static void SetNote(AvaloniaObject target, string value) => target.SetValue(NoteProperty, value);
    }

    private static class NestedAttachedOwner
    {
        [Category("Test properties")]
        [Description("A custom attached hint.")]
        public static readonly AttachedProperty<string> HintProperty =
            AvaloniaProperty.RegisterAttached<AvaloniaObject, string>("Hint", typeof(NestedAttachedOwner), "ready");

        public static string GetHint(AvaloniaObject target) => target.GetValue(HintProperty);

        public static void SetHint(AvaloniaObject target, string value) => target.SetValue(HintProperty, value);
    }
}
