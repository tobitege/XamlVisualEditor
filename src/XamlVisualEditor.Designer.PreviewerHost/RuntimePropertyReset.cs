using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using System.Globalization;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Designer.PreviewerHost;

/// <summary>Removes local Avalonia values so styles and inherited values apply again.</summary>
internal static class RuntimePropertyReset
{
    public static bool TryPrepareLayout(object control, string fragment, IReadOnlyList<string> cleared, out Action apply)
    {
        apply = static () => { };
        Type? avaloniaObject = Type.GetType("Avalonia.AvaloniaObject, Avalonia.Base");
        if (avaloniaObject?.IsInstanceOfType(control) != true
            || !DesignerUpdateProtocol.TryGetLayoutValues(fragment, cleared, out var values)) return false;
        Type propertyType = Type.GetType("Avalonia.AvaloniaProperty, Avalonia.Base", throwOnError: true)!;
        Type priorityType = Type.GetType("Avalonia.Data.BindingPriority, Avalonia.Base", throwOnError: true)!;
        MethodInfo set = avaloniaObject.GetMethod("SetValue", new[] { propertyType, typeof(object), priorityType })!;
        MethodInfo clear = avaloniaObject.GetMethod("ClearValue", new[] { propertyType })!;
        object priority = Enum.Parse(priorityType, "LocalValue");
        XElement element = XElement.Parse(fragment);
        List<(object Property, object? Value)> properties = new();
        try
        {
            foreach (var value in values)
            {
                object property = ResolveProperty(control, XName.Get(value.Key), element);
                Type valueType = (Type)propertyType.GetProperty("PropertyType")!.GetValue(property)!;
                if (valueType != typeof(double) && valueType != typeof(int)
                    && valueType.FullName is not ("Avalonia.Thickness" or "Avalonia.Layout.HorizontalAlignment"
                        or "Avalonia.Layout.VerticalAlignment" or "Avalonia.Controls.Dock")) return false;
                object? parsed = value.Value is null ? null
                    : valueType == typeof(double) ? double.Parse(value.Value, CultureInfo.InvariantCulture)
                    : valueType == typeof(int) ? int.Parse(value.Value, CultureInfo.InvariantCulture)
                    : valueType.IsEnum ? Enum.Parse(valueType, value.Value, true)
                    : valueType.GetMethod("Parse", new[] { typeof(string) })!.Invoke(null, new object[] { value.Value });
                properties.Add((property, parsed));
            }
        }
        catch (Exception error) when (error is NotSupportedException or FormatException or ArgumentException or OverflowException or TargetInvocationException) { return false; }
        apply = () =>
        {
            foreach (var property in properties)
            {
                if (property.Value is null) clear.Invoke(control, new[] { property.Property });
                else set.Invoke(control, new[] { property.Property, property.Value, priority });
            }
        };
        return true;
    }

    public static Action Prepare(object control, string fragment, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return static () => { };
        Type propertyType = Type.GetType("Avalonia.AvaloniaProperty, Avalonia.Base", throwOnError: true)!;
        MethodInfo clear = control.GetType().GetMethod("ClearValue", new[] { propertyType })!;
        XElement element = XElement.Parse(fragment);
        object[] properties = names.Select(name => ResolveProperty(control, XName.Get(name), element)).ToArray();
        return () => { foreach (object property in properties) clear.Invoke(control, new[] { property }); };
    }

    private static object ResolveProperty(object control, XName attribute, XElement element)
    {
        Type registryType = Type.GetType("Avalonia.AvaloniaPropertyRegistry, Avalonia.Base", throwOnError: true)!;
        Type propertyType = Type.GetType("Avalonia.AvaloniaProperty, Avalonia.Base", throwOnError: true)!;
        object registry = registryType.GetProperty("Instance")!.GetValue(null)!;
        MethodInfo find = registryType.GetMethod("FindRegistered", new[] { typeof(Type), typeof(string) })!;
        int separator = attribute.LocalName.IndexOf('.');
        object? property;
        if (separator < 0 && attribute.Namespace == XNamespace.None)
            property = find.Invoke(registry, new object[] { control.GetType(), attribute.LocalName });
        else
        {
            string ownerName = separator < 0 ? string.Empty : attribute.LocalName[..separator];
            string propertyName = attribute.LocalName[(separator + 1)..];
            string xmlNamespace = attribute.Namespace == XNamespace.None ? element.GetDefaultNamespace().NamespaceName : attribute.NamespaceName;
            Type? builtInOwner = Type.GetType("Avalonia.Controls." + ownerName + ", Avalonia.Controls");
            if (builtInOwner is not null && MatchesNamespace(builtInOwner, xmlNamespace)
                && builtInOwner.GetField(propertyName + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    ?.GetValue(null) is { } builtInProperty) return builtInProperty;
            IEnumerable attached = (IEnumerable)registryType.GetMethod("GetRegisteredAttached")!
                .Invoke(registry, new object[] { control.GetType() })!;
            object[] matches = attached.Cast<object>().Where(candidate =>
            {
                Type owner = (Type)propertyType.GetProperty("OwnerType")!.GetValue(candidate)!;
                return owner.Name == ownerName && (string)propertyType.GetProperty("Name")!.GetValue(candidate)! == propertyName
                    && MatchesNamespace(owner, xmlNamespace);
            }).Distinct().ToArray();
            property = matches.Length == 1 ? matches[0] : null;
        }
        return property ?? throw new NotSupportedException($"Property '{attribute}' cannot be reset in place.");
    }

    private static bool MatchesNamespace(Type owner, string xmlNamespace)
    {
        if (xmlNamespace.StartsWith("using:", StringComparison.Ordinal)) return owner.Namespace == xmlNamespace[6..];
        if (xmlNamespace.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            string[] parts = xmlNamespace[14..].Split(';');
            return owner.Namespace == parts[0] && (parts.Length == 1
                || parts.Length == 2 && parts[1] == "assembly=" + owner.Assembly.GetName().Name);
        }
        return owner.Assembly.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType.FullName == "Avalonia.Metadata.XmlnsDefinitionAttribute"
            && attribute.ConstructorArguments.Count == 2
            && (string?)attribute.ConstructorArguments[0].Value == xmlNamespace
            && (string?)attribute.ConstructorArguments[1].Value == owner.Namespace);
    }
}
