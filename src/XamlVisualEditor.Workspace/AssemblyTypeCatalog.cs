using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Avalonia.Controls;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Workspace;

// Reads types and inheritance from PE metadata without instantiating controls or foreign attributes.
internal static class AssemblyTypeCatalog
{
    internal readonly record struct TypeIdentity(string AssemblyName, string FullName);

    internal sealed record AssemblyIdentity(string Name, string Version, bool IsStandardAssembly);

    internal sealed record CatalogType(
        TypeIdentity Identity,
        string Namespace,
        string Name,
        TypeIdentity? BaseType,
        bool IsPublic,
        bool IsInstantiable,
        string? DisplayName,
        string? Category,
        bool IsToolboxVisible)
    {
        internal string? AssemblyVersion { get; init; }
        internal bool IsStandardAssembly { get; init; }
    }

    internal static AssemblyIdentity? ReadAssemblyIdentity(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader reader = new(stream);
        return reader.HasMetadata ? ReadAssemblyIdentity(reader.GetMetadataReader()) : null;
    }

    private static AssemblyIdentity ReadAssemblyIdentity(MetadataReader metadata)
    {
        if (!metadata.IsAssembly) throw new BadImageFormatException("The module has no assembly identity.");
        AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
        string name = metadata.GetString(assembly.Name);
        byte[] publicKey = metadata.GetBlobBytes(assembly.PublicKey);
        return new AssemblyIdentity(name, assembly.Version.ToString(), IsStandardAssembly(name, publicKey));
    }

    internal static bool IsStandardAssembly(string name, ReadOnlySpan<byte> publicKey)
    {
        byte[] officialKey = typeof(Control).Assembly.GetName().GetPublicKey() ?? Array.Empty<byte>();
        return (name == "Avalonia" || name.StartsWith("Avalonia.", StringComparison.Ordinal))
            && officialKey.Length > 0 && publicKey.SequenceEqual(officialKey);
    }

    internal static IReadOnlyList<TypeMetadata> Read(
        IEnumerable<string> assemblyPaths,
        Action<string> reportFailure,
        CancellationToken cancellationToken = default,
        Action<int, int, string>? reportProgress = null)
    {
        Dictionary<TypeIdentity, CatalogType> definitions = new();
        string[] paths = assemblyPaths.ToArray();
        for (int index = 0; index < paths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = paths[index];
            reportProgress?.Invoke(index, paths.Length, Path.GetFileName(path));
            try
            {
                using FileStream stream = File.OpenRead(path);
                foreach (CatalogType type in ReadMetadata(stream, cancellationToken))
                {
                    definitions[type.Identity] = type;
                }
            }
            catch (BadImageFormatException)
            {
                // Native assemblies and assemblies unsuitable for this metadata search are skipped.
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                reportFailure(string.Format(UiStrings.ControlMetadataReadFailed, path, error.Message));
            }
        }

        reportProgress?.Invoke(paths.Length, paths.Length, string.Empty);

        TypeIdentity controlType = IdentityOf(typeof(Control));
        TypeIdentity styleType = IdentityOf(typeof(Avalonia.Styling.Styles));
        TypeIdentity panelType = IdentityOf(typeof(Panel));
        TypeIdentity contentType = IdentityOf(typeof(ContentControl));
        TypeIdentity itemsType = IdentityOf(typeof(ItemsControl));
        TypeIdentity topLevelType = IdentityOf(typeof(TopLevel));
        List<TypeMetadata> result = new();
        foreach (CatalogType type in definitions.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!type.IsPublic)
            {
                continue;
            }

            result.Add(new TypeMetadata
            {
                FullName = type.Identity.FullName,
                Name = type.Name,
                XmlNamespace = $"clr-namespace:{type.Namespace};assembly={type.Identity.AssemblyName}",
                ClrNamespace = type.Namespace,
                AssemblyName = type.Identity.AssemblyName,
                AssemblyVersion = type.AssemblyVersion,
                IsControl = DerivesFrom(type, controlType, definitions),
                IsStyle = DerivesFrom(type, styleType, definitions),
                IsStandardControl = type.IsStandardAssembly,
                IsPanel = DerivesFrom(type, panelType, definitions),
                IsContentControl = DerivesFrom(type, contentType, definitions),
                IsItemsControl = DerivesFrom(type, itemsType, definitions),
                IsTopLevel = DerivesFrom(type, topLevelType, definitions),
                IsInstantiable = type.IsInstantiable,
                DisplayName = type.DisplayName,
                Category = type.Category,
                IsToolboxVisible = type.IsToolboxVisible
            });
        }

        return result.OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ThenBy(type => type.AssemblyName, StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<CatalogType> ReadMetadata(Stream stream, CancellationToken cancellationToken = default)
    {
        using PEReader reader = new(stream, PEStreamOptions.LeaveOpen);
        if (!reader.HasMetadata)
        {
            return Array.Empty<CatalogType>();
        }

        MetadataReader metadata = reader.GetMetadataReader();
        AssemblyIdentity assembly = ReadAssemblyIdentity(metadata);
        string assemblyName = assembly.Name;
        List<CatalogType> result = new();
        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TypeDefinition definition = metadata.GetTypeDefinition(handle);
            TypeIdentity identity = ResolveIdentity(metadata, handle, assemblyName)!.Value;
            bool isPublic = (definition.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public;
            bool isInstantiable = isPublic
                && (definition.Attributes & TypeAttributes.Abstract) == 0
                && definition.GetGenericParameters().Count == 0
                && HasDefaultConstructor(metadata, definition);
            (string? displayName, string? category, bool visible) = ReadDesignAttributes(metadata, definition, assemblyName);
            result.Add(new CatalogType(identity, metadata.GetString(definition.Namespace), metadata.GetString(definition.Name),
                ResolveIdentity(metadata, definition.BaseType, assemblyName), isPublic, isInstantiable,
                displayName, category, visible)
            {
                AssemblyVersion = assembly.Version,
                IsStandardAssembly = assembly.IsStandardAssembly
            });
        }

        return result;
    }

    internal static IReadOnlyList<(string XmlNamespace, string ClrNamespace)> ReadXmlnsDefinitions(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader reader = new(stream);
        MetadataReader metadata = reader.GetMetadataReader();
        AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
        string assemblyName = metadata.GetString(assembly.Name);
        List<(string, string)> result = new();
        foreach (CustomAttributeHandle handle in assembly.GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            EntityHandle owner = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                _ => default
            };
            if (ResolveIdentity(metadata, owner, assemblyName)?.FullName != "Avalonia.Metadata.XmlnsDefinitionAttribute")
                continue;
            BlobReader value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1) continue;
            string? xmlNamespace = value.ReadSerializedString();
            string? clrNamespace = value.ReadSerializedString();
            if (!string.IsNullOrWhiteSpace(xmlNamespace) && !string.IsNullOrWhiteSpace(clrNamespace))
                result.Add((xmlNamespace, clrNamespace));
        }
        return result;
    }

    private static TypeIdentity IdentityOf(Type type)
    {
        return new TypeIdentity(type.Assembly.GetName().Name!, type.FullName!);
    }

    private static bool DerivesFrom(CatalogType type, TypeIdentity target, IReadOnlyDictionary<TypeIdentity, CatalogType> definitions)
    {
        HashSet<TypeIdentity> visited = new();
        while (visited.Add(type.Identity))
        {
            if (type.Identity == target)
            {
                return true;
            }
            if (type.BaseType is not { } parent || !definitions.TryGetValue(parent, out CatalogType? parentType))
            {
                return false;
            }
            type = parentType;
        }
        return false;
    }

    private static TypeIdentity? ResolveIdentity(MetadataReader metadata, EntityHandle handle, string assemblyName)
    {
        if (handle.IsNil)
        {
            return null;
        }
        if (handle.Kind == HandleKind.TypeSpecification)
        {
            BlobReader signature = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
            SignatureTypeCode code = signature.ReadSignatureTypeCode();
            while (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
            {
                signature.ReadTypeHandle();
                code = signature.ReadSignatureTypeCode();
            }
            if (code == SignatureTypeCode.GenericTypeInstance)
            {
                code = signature.ReadSignatureTypeCode();
            }
            return code == SignatureTypeCode.TypeHandle
                ? ResolveIdentity(metadata, signature.ReadTypeHandle(), assemblyName)
                : null;
        }
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            TypeDefinition type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
            string name = metadata.GetString(type.Name);
            TypeIdentity? parent = ResolveIdentity(metadata, type.GetDeclaringType(), assemblyName);
            string typeNamespace = metadata.GetString(type.Namespace);
            return new TypeIdentity(assemblyName, parent is { } owner
                ? owner.FullName + "+" + name
                : string.IsNullOrEmpty(typeNamespace) ? name : typeNamespace + "." + name);
        }
        if (handle.Kind == HandleKind.TypeReference)
        {
            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)handle);
            string name = metadata.GetString(type.Name);
            EntityHandle scope = type.ResolutionScope;
            if (scope.Kind == HandleKind.TypeReference && ResolveIdentity(metadata, scope, assemblyName) is { } owner)
            {
                return new TypeIdentity(owner.AssemblyName, owner.FullName + "+" + name);
            }
            string ownerAssembly = scope.Kind == HandleKind.AssemblyReference
                ? metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
                : assemblyName;
            string typeNamespace = metadata.GetString(type.Namespace);
            return new TypeIdentity(ownerAssembly, string.IsNullOrEmpty(typeNamespace) ? name : typeNamespace + "." + name);
        }
        return null;
    }

    private static bool HasDefaultConstructor(MetadataReader metadata, TypeDefinition type)
    {
        foreach (MethodDefinitionHandle handle in type.GetMethods())
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (!metadata.StringComparer.Equals(method.Name, ".ctor")
                || (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
            {
                continue;
            }
            BlobReader signature = metadata.GetBlobReader(method.Signature);
            SignatureHeader header = signature.ReadSignatureHeader();
            if (header.IsGeneric)
            {
                signature.ReadCompressedInteger();
            }
            if (signature.ReadCompressedInteger() == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static (string? DisplayName, string? Category, bool Visible) ReadDesignAttributes(
        MetadataReader metadata, TypeDefinition type, string assemblyName)
    {
        string? displayName = null;
        string? category = null;
        bool visible = true;
        foreach (CustomAttributeHandle handle in type.GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            EntityHandle owner = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                _ => default
            };
            string? attributeName = ResolveIdentity(metadata, owner, assemblyName)?.FullName;
            if (attributeName is not ("System.ComponentModel.DisplayNameAttribute"
                or "System.ComponentModel.CategoryAttribute" or "System.ComponentModel.DesignTimeVisibleAttribute"))
            {
                continue;
            }
            BlobReader value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1)
            {
                continue;
            }
            switch (attributeName)
            {
                case "System.ComponentModel.DisplayNameAttribute":
                    displayName = value.ReadSerializedString();
                    break;
                case "System.ComponentModel.CategoryAttribute":
                    category = value.ReadSerializedString();
                    break;
                case "System.ComponentModel.DesignTimeVisibleAttribute":
                    visible = value.ReadByte() != 0;
                    break;
            }
        }
        return (displayName, category, visible);
    }
}
