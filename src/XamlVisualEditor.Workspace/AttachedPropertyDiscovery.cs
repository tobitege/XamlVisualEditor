using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace XamlVisualEditor.Workspace;

/// <summary>
/// Finds potential attached-property fields without loading unrelated CLR types.
/// </summary>
internal static class AttachedPropertyDiscovery
{
    internal static IReadOnlyList<AttachedPropertyField> Read(Stream stream)
    {
        using PEReader reader = new(stream, PEStreamOptions.LeaveOpen);
        if (!reader.HasMetadata)
        {
            return Array.Empty<AttachedPropertyField>();
        }

        MetadataReader metadata = reader.GetMetadataReader();
        List<AttachedPropertyField> fields = new();
        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            string? ownerName = null;
            foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
            {
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public ||
                    (field.Attributes & FieldAttributes.Static) == 0 ||
                    !IsPropertyField(metadata, field))
                {
                    continue;
                }

                ownerName ??= GetOwnerName(metadata, handle);
                if (ownerName is null)
                {
                    break;
                }

                fields.Add(new AttachedPropertyField(ownerName, metadata.GetString(field.Name)));
            }
        }

        return fields;
    }

    private static bool IsPropertyField(MetadataReader metadata, FieldDefinition field)
    {
        BlobReader signature = metadata.GetBlobReader(field.Signature);
        if (signature.ReadSignatureHeader().Kind != SignatureKind.Field)
        {
            return false;
        }

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

        if (code != SignatureTypeCode.TypeHandle)
        {
            return false;
        }

        EntityHandle typeHandle = signature.ReadTypeHandle();
        StringHandle typeNamespace;
        StringHandle typeName;
        if (typeHandle.Kind == HandleKind.TypeReference)
        {
            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)typeHandle);
            typeNamespace = type.Namespace;
            typeName = type.Name;
        }
        else if (typeHandle.Kind == HandleKind.TypeDefinition)
        {
            TypeDefinition type = metadata.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
            typeNamespace = type.Namespace;
            typeName = type.Name;
        }
        else
        {
            return false;
        }

        // Attached properties may be declared using either of their base types.
        // Inspect only the outer field type; generic arguments need not be loaded.
        return metadata.StringComparer.Equals(typeNamespace, "Avalonia") &&
            (metadata.StringComparer.Equals(typeName, "AttachedProperty`1") ||
             metadata.StringComparer.Equals(typeName, "StyledProperty`1") ||
             metadata.StringComparer.Equals(typeName, "AvaloniaProperty"));
    }

    private static string? GetOwnerName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        string? name = null;
        while (!handle.IsNil)
        {
            TypeDefinition type = metadata.GetTypeDefinition(handle);
            if (type.GetGenericParameters().Count != 0)
            {
                return null;
            }

            string currentName = metadata.GetString(type.Name);
            name = name is null ? currentName : currentName + "+" + name;
            handle = type.GetDeclaringType();
            if (handle.IsNil)
            {
                string typeNamespace = metadata.GetString(type.Namespace);
                return string.IsNullOrEmpty(typeNamespace) ? name : typeNamespace + "." + name;
            }
        }

        return name;
    }
}

internal readonly record struct AttachedPropertyField(string OwnerTypeName, string FieldName);
