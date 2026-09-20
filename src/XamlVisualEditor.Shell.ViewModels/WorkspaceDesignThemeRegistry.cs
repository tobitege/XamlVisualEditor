using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Metadata;
using Avalonia.Platform;
using Avalonia.Styling;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>
/// Holds factories for the workspace application's styles and resource includes so
/// the design surface can theme instantiated controls the same way the target app
/// does at runtime. Factories (instead of shared instances) because every design
/// surface needs its own style instances.
/// </summary>
public static class WorkspaceDesignThemeRegistry
{
    public static int Version { get; private set; }

    public static IReadOnlyList<Func<IStyle?>> StyleFactories { get; private set; } =
        Array.Empty<Func<IStyle?>>();

    public static IReadOnlyList<Func<IResourceProvider?>> ResourceFactories { get; private set; } =
        Array.Empty<Func<IResourceProvider?>>();

    public static void Update(
        IReadOnlyList<Func<IStyle?>> styleFactories,
        IReadOnlyList<Func<IResourceProvider?>> resourceFactories)
    {
        StyleFactories = styleFactories;
        ResourceFactories = resourceFactories;
        Version++;
    }

    public static void Clear()
    {
        Update(Array.Empty<Func<IStyle?>>(), Array.Empty<Func<IResourceProvider?>>());
    }
}

/// <summary>
/// Loads application styles and resources as one independently owned XAML scope.
/// </summary>
public static class WorkspaceDesignThemeLoader
{
    private static readonly ConditionalWeakTable<IStyle, IReadOnlyList<IDataTemplate>> ApplicationTemplates = new();

    /// <summary>Gets templates created in the same resource scope as an application style instance.</summary>
    public static IReadOnlyList<IDataTemplate> GetApplicationDataTemplates(IStyle style) =>
        ApplicationTemplates.TryGetValue(style, out IReadOnlyList<IDataTemplate>? templates)
            ? templates : Array.Empty<IDataTemplate>();

    /// <summary>
    /// Registers complete application style and resource declarations without running application startup code.
    /// Returns human-readable detail lines for the output log.
    /// </summary>
    public static IReadOnlyList<string> LoadFromApplicationXaml(string appXamlPath)
    {
        XDocument document = XDocument.Load(appXamlPath);
        XElement? root = document.Root;
        if (root is null)
        {
            WorkspaceDesignThemeRegistry.Clear();
            return Array.Empty<string>();
        }

        XElement? stylesElement = root.Elements()
            .FirstOrDefault(e => e.Name.LocalName.EndsWith(".Styles", StringComparison.Ordinal));
        XElement? resourcesElement = root.Elements()
            .FirstOrDefault(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal));
        XElement? templatesElement = root.Elements()
            .FirstOrDefault(e => e.Name.LocalName.EndsWith(".DataTemplates", StringComparison.Ordinal));
        bool hasStyles = stylesElement?.Elements().Any() == true;
        bool hasTemplates = templatesElement?.Elements().Any() == true;
        if (!hasStyles && !hasTemplates && resourcesElement is null)
        {
            WorkspaceDesignThemeRegistry.Clear();
            return Array.Empty<string>();
        }

        XNamespace avalonia = "https://github.com/avaloniaui";
        string prefix = "xveTheme";
        for (int suffix = 1; root.GetNamespaceOfPrefix(prefix) is not null; suffix++) prefix = "xveTheme" + suffix;
        XElement bundle = new(avalonia + "Styles",
            root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration).Select(attribute => new XAttribute(attribute)),
            new XAttribute(XNamespace.Xmlns + prefix, avalonia.NamespaceName));
        if (resourcesElement is not null)
        {
            XElement resources = new(resourcesElement) { Name = avalonia + "Styles.Resources" };
            bundle.Add(resources);
        }
        List<string> templateKeys = new();
        if (hasTemplates)
        {
            XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
            HashSet<string> reservedPrefixes = root.DescendantsAndSelf().Attributes()
                .Where(attribute => attribute.IsNamespaceDeclaration)
                .Select(attribute => attribute.Name.LocalName).ToHashSet(StringComparer.Ordinal);
            string keyPrefix = "xveTemplateKey";
            for (int suffix = 1; reservedPrefixes.Contains(keyPrefix); suffix++) keyPrefix = "xveTemplateKey" + suffix;
            bundle.Add(new XAttribute(XNamespace.Xmlns + keyPrefix, xamlNamespace.NamespaceName));
            XElement dictionary = new(avalonia + "ResourceDictionary");
            foreach (XElement source in templatesElement!.Elements())
            {
                string key = "__xve_template_" + Guid.NewGuid().ToString("N");
                XElement template = new(source);
                template.SetAttributeValue(xamlNamespace + "Key", key);
                dictionary.Add(template);
                templateKeys.Add(key);
            }
            bundle.Add(new XElement(avalonia + "Styles",
                templatesElement.Attributes().Select(attribute => new XAttribute(attribute)),
                new XElement(avalonia + "Styles.Resources", dictionary)));
        }
        if (hasStyles)
        {
            XElement styles = new(stylesElement!) { Name = avalonia + "Styles" };
            bundle.Add(styles);
        }

        string? className = root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
        string sourcePath = Path.GetFullPath(appXamlPath);
        Styles CreateScope()
        {
            Assembly? assembly = ResolveApplicationAssembly(className);
            Uri uri = ResolveApplicationUri(sourcePath, assembly);
            string xaml = QualifyElementTypes(new XElement(bundle)).ToString(SaveOptions.DisableFormatting);
            Styles scope = AvaloniaRuntimeXamlLoader.Load(xaml, assembly, null, uri, designMode: true) as Styles
                ?? throw new InvalidOperationException($"Application themes in {sourcePath} did not produce a Styles scope.");
            if (templateKeys.Count > 0)
            {
                Styles carrier = (Styles)scope[0];
                IDataTemplate[] templates = templateKeys.Select(key => carrier.Resources[key] as IDataTemplate
                    ?? throw new InvalidOperationException($"An application data template in {sourcePath} does not implement IDataTemplate.")).ToArray();
                ApplicationTemplates.Add(scope, templates);
            }
            return scope;
        }

        WorkspaceDesignThemeRegistry.Update(
            hasStyles || hasTemplates ? new Func<IStyle?>[] { CreateScope } : Array.Empty<Func<IStyle?>>(),
            hasStyles || hasTemplates ? Array.Empty<Func<IResourceProvider?>>() : new Func<IResourceProvider?>[] { CreateScope });
        return new[] { "Application styles, resources and data templates registered as one XAML scope." };
    }

    private static XElement QualifyElementTypes(XElement root)
    {
        Dictionary<string, List<(Assembly Assembly, string Namespace)>> mappings = new(StringComparer.Ordinal);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(candidate => !candidate.IsDynamic))
        {
            foreach (CustomAttributeData attribute in assembly.GetCustomAttributesData())
            {
                if (attribute.AttributeType != typeof(XmlnsDefinitionAttribute) || attribute.ConstructorArguments.Count != 2 ||
                    attribute.ConstructorArguments[0].Value is not string xmlNamespace ||
                    attribute.ConstructorArguments[1].Value is not string clrNamespace) continue;
                if (!mappings.TryGetValue(xmlNamespace, out List<(Assembly Assembly, string Namespace)>? entries))
                    mappings[xmlNamespace] = entries = new();
                entries.Add((assembly, clrNamespace));
            }
        }

        HashSet<string> prefixes = root.DescendantsAndSelf().Attributes()
            .Where(attribute => attribute.IsNamespaceDeclaration)
            .Select(attribute => attribute.Name.LocalName).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> qualifiedNamespaces = new(StringComparer.Ordinal);
        foreach (XElement element in root.DescendantsAndSelf())
        {
            if (!mappings.TryGetValue(element.Name.NamespaceName, out var candidates)) continue;
            string name = element.Name.LocalName;
            int separator = name.IndexOf('.');
            string typeName = separator < 0 ? name : name[..separator];
            Type[] types = candidates.Select(candidate => candidate.Assembly.GetType(
                    candidate.Namespace + "." + typeName, throwOnError: false))
                .OfType<Type>().Distinct().ToArray();
            if (types.Length == 0) continue;
            if (types.Length > 1)
                throw new InvalidOperationException($"Theme element {element.Name} is ambiguous; use an assembly-qualified CLR namespace.");

            Type type = types[0];
            string qualifiedNamespace = $"clr-namespace:{type.Namespace};assembly={type.Assembly.GetName().Name}";
            if (!qualifiedNamespaces.ContainsKey(qualifiedNamespace))
            {
                string prefix;
                int suffix = qualifiedNamespaces.Count;
                do { prefix = "xveType" + suffix++; } while (!prefixes.Add(prefix));
                root.Add(new XAttribute(XNamespace.Xmlns + prefix, qualifiedNamespace));
                qualifiedNamespaces[qualifiedNamespace] = prefix;
            }
            element.Name = XName.Get(name, qualifiedNamespace);
        }
        return root;
    }

    private static Assembly? ResolveApplicationAssembly(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        Assembly[] matches = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetType(className, throwOnError: false) is not null).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException($"The application type {className} is present in multiple loaded assemblies.");
        return matches.SingleOrDefault();
    }

    private static Uri ResolveApplicationUri(string sourcePath, Assembly? assembly)
    {
        if (assembly?.GetName().Name is { } assemblyName)
        {
            Uri assetRoot = new($"avares://{assemblyName}/");
            string fileName = Path.GetFileName(sourcePath);
            Uri[] matches = AssetLoader.GetAssets(assetRoot, null)
                .Where(uri => string.Equals(Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)),
                    fileName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length == 1) return matches[0];
            if (matches.Length > 1)
                throw new InvalidOperationException($"Multiple application XAML assets match {sourcePath}; relative theme sources are ambiguous.");
        }
        return new Uri(sourcePath, UriKind.Absolute);
    }
}
