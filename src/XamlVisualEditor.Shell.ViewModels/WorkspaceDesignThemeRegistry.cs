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
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Metadata;
using Avalonia.Platform;
using Avalonia.Styling;
using XamlVisualEditor.Core.Localization;

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
    /// <summary>
    /// Name of the environment variable that names the application XAML file whose styles
    /// and resources the design surface should apply. It takes precedence over the
    /// workspace scan, which finds nothing for library workspaces: their application
    /// (and with it the control themes) lives outside the loaded project graph.
    /// </summary>
    public const string ApplicationXamlOverrideVariable = "XVE_DESIGN_APP_XAML";

    private static readonly ConditionalWeakTable<IStyle, IReadOnlyList<IDataTemplate>> ApplicationTemplates = new();

    /// <summary>
    /// Resolves the application XAML override from the environment. Returns the full
    /// path when the variable is set and the file exists; otherwise null.
    /// </summary>
    public static string? GetApplicationXamlOverridePath()
    {
        string? value = Environment.GetEnvironmentVariable(ApplicationXamlOverrideVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(value.Trim());
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Gets templates created in the same resource scope as an application style instance.</summary>
    public static IReadOnlyList<IDataTemplate> GetApplicationDataTemplates(IStyle style) =>
        ApplicationTemplates.TryGetValue(style, out IReadOnlyList<IDataTemplate>? templates)
            ? templates : Array.Empty<IDataTemplate>();

    /// <summary>
    /// Registers complete application style and resource declarations without running application startup code.
    /// Returns human-readable detail lines for the output log. When the declarations cannot be compiled as one
    /// scope in the editor process (for example because the application assembly or one of its libraries is not
    /// loaded there), the design surface applies the loadable includes and theme elements one by one instead;
    /// <paramref name="report"/> receives the skipped parts once.
    /// </summary>
    public static IReadOnlyList<string> LoadFromApplicationXaml(string appXamlPath, Action<string>? report = null)
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

        bool loadItemByItem = false;
        Styles CreateScopeOrItems()
        {
            if (!loadItemByItem)
            {
                try
                {
                    return CreateScope();
                }
                catch (Exception exception)
                {
                    loadItemByItem = true;
                    report?.Invoke(string.Format(UiStrings.DesignThemesScopeFailed, sourcePath, FirstMessage(exception)));
                    return CreateItemScope(root, resourcesElement, stylesElement, sourcePath, report);
                }
            }

            return CreateItemScope(root, resourcesElement, stylesElement, sourcePath, report: null);
        }

        WorkspaceDesignThemeRegistry.Update(
            hasStyles || hasTemplates ? new Func<IStyle?>[] { CreateScopeOrItems } : Array.Empty<Func<IStyle?>>(),
            hasStyles || hasTemplates ? Array.Empty<Func<IResourceProvider?>>() : new Func<IResourceProvider?>[] { CreateScopeOrItems });
        return new[] { "Application styles, resources and data templates registered as one XAML scope." };
    }

    /// <summary>
    /// Builds a scope from the parts of an application XAML that load on their own: absolute resource and style
    /// includes are resolved from their precompiled assemblies, other style elements (themes, inline styles) are
    /// compiled individually. Parts that fail are skipped; data templates need the application scope and are skipped.
    /// </summary>
    private static Styles CreateItemScope(XElement root, XElement? resourcesElement, XElement? stylesElement,
        string sourcePath, Action<string>? report)
    {
        Uri baseUri = new(sourcePath, UriKind.Absolute);
        Styles scope = new();
        List<string> skipped = new();

        foreach (XElement include in resourcesElement?.Descendants().Where(element => element.Name.LocalName == "ResourceInclude")
                     ?? Enumerable.Empty<XElement>())
        {
            string? source = include.Attribute("Source")?.Value;
            TryAdd(source, () =>
            {
                ResourceInclude resource = new(baseUri) { Source = ParseAbsoluteSource(source) };
                _ = resource.Loaded;
                scope.Resources.MergedDictionaries.Add(resource);
            });
        }

        XNamespace avalonia = "https://github.com/avaloniaui";
        foreach (XElement element in stylesElement?.Elements() ?? Enumerable.Empty<XElement>())
        {
            string localName = element.Name.LocalName;
            if (localName == "FluentTheme")
            {
                // The editor provides the Fluent theme itself.
                continue;
            }

            if (localName == "StyleInclude")
            {
                string? source = element.Attribute("Source")?.Value;
                TryAdd(source, () =>
                {
                    StyleInclude style = new(baseUri) { Source = ParseAbsoluteSource(source) };
                    _ = style.Loaded;
                    scope.Add(style);
                });
                continue;
            }

            TryAdd(localName, () =>
            {
                XElement wrapper = new(avalonia + "Styles",
                    root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration).Select(attribute => new XAttribute(attribute)),
                    new XElement(element));
                string xaml = QualifyElementTypes(wrapper).ToString(SaveOptions.DisableFormatting);
                scope.Add((IStyle)AvaloniaRuntimeXamlLoader.Load(xaml, null, null, baseUri, designMode: true));
            });
        }

        if (report is not null && skipped.Count > 0)
        {
            report(string.Format(UiStrings.DesignThemesSkipped, sourcePath, string.Join("; ", skipped)));
        }

        return scope;

        void TryAdd(string? label, Action add)
        {
            try
            {
                add();
            }
            catch (Exception exception)
            {
                skipped.Add($"{label} ({FirstMessage(exception)})");
            }
        }
    }

    private static Uri ParseAbsoluteSource(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            ? uri
            : throw new InvalidOperationException("Only absolute include sources can be loaded outside the application scope.");

    // Reflection and type initializer wrappers hide the actual cause in their inner exception.
    private static string FirstMessage(Exception exception)
    {
        while (exception is AggregateException or TargetInvocationException or TypeInitializationException
               && exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception.Message;
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
