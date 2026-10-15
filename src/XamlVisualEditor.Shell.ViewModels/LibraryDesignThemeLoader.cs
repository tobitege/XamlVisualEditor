using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Styling;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Discovers default styles for control libraries without an application theme entry point.</summary>
public static class LibraryDesignThemeLoader
{
    /// <summary>
    /// Creates independent styles for libraries used by the actual control tree, including base controls.
    /// Libraries with several public style providers require an explicit application style selection.
    /// </summary>
    public static IReadOnlyList<IStyle> CreateStyles(Control root, ITypeMetadataService metadata,
        Action<string> reportDiagnostic)
    {
        HashSet<string> configuredAssemblies = ConfiguredStyleAssemblies(root.Styles);
        List<Assembly> assemblies = new();
        HashSet<Assembly> seen = new();
        foreach (Control control in new[] { root }.Concat(root.GetLogicalDescendants().OfType<Control>()))
        {
            Stack<Type> hierarchy = new();
            for (Type? type = control.GetType(); type is not null; type = type.BaseType) hierarchy.Push(type);
            while (hierarchy.TryPop(out Type? type))
            {
                Assembly assembly = type.Assembly;
                if (assembly != typeof(object).Assembly && assembly != typeof(AvaloniaObject).Assembly
                    && assembly != typeof(Control).Assembly && seen.Add(assembly)) assemblies.Add(assembly);
            }
        }

        TypeMetadata[] providers = metadata.GetAvailableTypes()
            .Where(type => type.IsStyle && type.IsInstantiable).ToArray();
        List<IStyle> styles = new();
        foreach (Assembly assembly in assemblies)
        {
            string? assemblyName = assembly.GetName().Name;
            if (assemblyName is not null && configuredAssemblies.Contains(assemblyName)) continue;
            TypeMetadata[] candidates = providers.Where(type =>
                string.Equals(type.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 1)
            {
                Type styleType = metadata.ResolveClrType(candidates[0])
                    ?? throw new InvalidOperationException($"Cannot resolve library theme {candidates[0].FullName}.");
                if (Activator.CreateInstance(styleType) is not IStyle style)
                    throw new InvalidOperationException($"Library theme {styleType.FullName} did not produce styles.");
                styles.Add(style);
                reportDiagnostic($"Library theme: {styleType.FullName}");
            }
            else if (candidates.Length > 1)
            {
                reportDiagnostic($"Library {assemblyName} has multiple themes; specify the desired theme in application styles: "
                    + string.Join(", ", candidates.Select(type => type.FullName)));
            }
            else
            {
                Uri source = new($"avares://{assemblyName}/Themes/Generic.axaml");
                if (AssetLoader.Exists(source))
                {
                    styles.Add(new StyleInclude(source) { Source = source });
                    reportDiagnostic($"Library styles: {source}");
                }
            }
        }
        return styles;
    }

    private static HashSet<string> ConfiguredStyleAssemblies(IEnumerable<IStyle> roots)
    {
        HashSet<string> assemblies = new(StringComparer.OrdinalIgnoreCase);
        HashSet<IStyle> visited = new(ReferenceEqualityComparer.Instance);
        Stack<IStyle> pending = new(roots);
        while (pending.TryPop(out IStyle? style))
        {
            if (!visited.Add(style)) continue;
            if (style.GetType().Assembly.GetName().Name is { } assemblyName) assemblies.Add(assemblyName);
            if (style is StyleInclude include)
            {
                if (include.Source is { IsAbsoluteUri: true } source && source.Scheme == "avares")
                    assemblies.Add(source.Host);
                if (include.Loaded is { } loaded) pending.Push(loaded);
            }
            if (style is IEnumerable<IStyle> children)
                foreach (IStyle child in children) pending.Push(child);
        }
        return assemblies;
    }
}
