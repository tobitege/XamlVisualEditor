using System.Collections;
using System.Reflection;
using System.Xml.Linq;

namespace XamlVisualEditor.Designer.PreviewerHost;

/// <summary>Entfernt lokale Avalonia-Werte, damit Styles und geerbte Werte wieder gelten.</summary>
internal static class RuntimePropertyReset
{
    public static Action Prepare(object control, string fragment, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return static () => { };
        Type registryType = Type.GetType("Avalonia.AvaloniaPropertyRegistry, Avalonia.Base", throwOnError: true)!;
        Type propertyType = Type.GetType("Avalonia.AvaloniaProperty, Avalonia.Base", throwOnError: true)!;
        object registry = registryType.GetProperty("Instance")!.GetValue(null)!;
        MethodInfo find = registryType.GetMethod("FindRegistered", new[] { typeof(Type), typeof(string) })!;
        MethodInfo clear = control.GetType().GetMethod("ClearValue", new[] { propertyType })!;
        XElement element = XElement.Parse(fragment);
        List<object> properties = new();
        foreach (string name in names)
        {
            XName attribute = XName.Get(name);
            int separator = attribute.LocalName.IndexOf('.');
            object? property;
            if (separator < 0 && attribute.Namespace == XNamespace.None)
                property = find.Invoke(registry, new object[] { control.GetType(), attribute.LocalName });
            else
            {
                string ownerName = separator < 0 ? string.Empty : attribute.LocalName[..separator];
                string propertyName = attribute.LocalName[(separator + 1)..];
                string xmlNamespace = attribute.Namespace == XNamespace.None ? element.GetDefaultNamespace().NamespaceName : attribute.NamespaceName;
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
            if (property is null) throw new NotSupportedException($"Property '{name}' cannot be reset in place.");
            properties.Add(property);
        }
        return () => { foreach (object property in properties) clear.Invoke(control, new[] { property }); };
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
