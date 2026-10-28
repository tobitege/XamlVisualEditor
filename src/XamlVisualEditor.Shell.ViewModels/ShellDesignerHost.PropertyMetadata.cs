using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Language;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class ShellDesignerHost
{
    private static MutableAstPropertyNode? FindLocalProperty(
        DesignerDocumentViewModel document, MutableAstObjectNode node, string name)
    {
        if (name == "Name" && XamlNames.FindNameProperty(node, document.MetadataService) is { } declaration)
            return declaration;
        MutableAstPropertyNode? exact = node.Properties.FirstOrDefault(property => property.PropertyName == name);
        if (exact is not null || name.Contains('.') || name.Contains(':')) return exact;
        var metadata = document.MetadataService;
        TypeMetadata? type = metadata?.GetType(node.XmlNamespace, node.TypeName);
        if (metadata is null || type is null) return node.FindProperty(name);
        Type? clrType = metadata.ResolveClrType(type);
        if (clrType is null) return node.FindProperty(name);
        for (Type? owner = clrType; owner is not null; owner = owner.BaseType)
        {
            string qualified = owner.Name + "." + name;
            foreach (MutableAstPropertyNode property in node.Properties.Where(property => property.PropertyName == qualified))
            {
                if (string.IsNullOrEmpty(property.XmlNamespace)) return property;
                TypeMetadata? declaredOwner = metadata.GetType(property.XmlNamespace, owner.Name);
                if (declaredOwner is not null && declaredOwner.FullName == owner.FullName
                    && declaredOwner.AssemblyName == owner.Assembly.GetName().Name) return property;
            }
        }
        return null;
    }

    private static IReadOnlyList<DesignerPropertyInfo> AddPropertyTypeSemantics(
        DesignerDocumentViewModel document, MutableAstObjectNode node, IReadOnlyList<DesignerPropertyInfo> properties)
    {
        TypeMetadata? owner = document.MetadataService?.GetType(node.XmlNamespace, node.TypeName);
        if (owner is null) return properties;
        IReadOnlyList<PropertyMetadata> metadata = document.MetadataService!.GetProperties(owner);
        Dictionary<(string Name, string? Owner), PropertyMetadata> declarations = new();
        foreach (PropertyMetadata property in metadata)
            declarations.TryAdd((property.Name, property.IsAttached ? property.OwnerType : null), property);

        return properties.Select(property =>
        {
            if (!declarations.TryGetValue((property.Name, property.IsAttached ? property.OwnerType : null), out PropertyMetadata? declaration)
                || declaration.ClrType is not { } declaredType) return property;
            Type type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            bool canAssignNull = !property.IsReadOnly &&
                (!declaredType.IsValueType || Nullable.GetUnderlyingType(declaredType) is not null) &&
                !(owner.IsControl && property.Name == "Name");
            bool collection = type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);
            bool template = typeof(Avalonia.Controls.Templates.IControlTemplate).IsAssignableFrom(type)
                || typeof(Avalonia.Controls.Templates.IDataTemplate).IsAssignableFrom(type)
                || type.GetInterfaces().Append(type).Any(contract => contract.IsGenericType
                    && contract.Name is "ITemplate`1" or "ITemplate`2"
                    && contract.Namespace is "Avalonia.Controls" or "Avalonia.Controls.Templates"
                    && (contract.Assembly == typeof(Avalonia.Controls.Control).Assembly || contract.Assembly == typeof(Avalonia.AvaloniaObject).Assembly));
            bool flags = type.IsEnum && type.IsDefined(typeof(FlagsAttribute), inherit: false);
            if (!flags) return property with { IsCollection = collection, IsTemplate = template, CanAssignNull = canAssignNull };

            Type underlyingType = Enum.GetUnderlyingType(type);
            bool signed = underlyingType == typeof(sbyte) || underlyingType == typeof(short)
                || underlyingType == typeof(int) || underlyingType == typeof(long);
            Dictionary<string, ulong> values = new(StringComparer.Ordinal);
            foreach (string name in Enum.GetNames(type))
            {
                object value = Enum.Parse(type, name);
                values[name] = signed ? unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture))
                    : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            return property with { IsCollection = collection, IsFlagsEnum = true, IsSignedEnum = signed, EnumValues = values,
                CanAssignNull = canAssignNull };
        }).ToArray();
    }
}
