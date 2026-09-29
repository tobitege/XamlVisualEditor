using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Platform;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.App.Services;

/// <summary>Keeps providers and their private dependencies outside the assembly context of the editor and the preview.</summary>
internal sealed class EditorThemeProviderLoader : AssemblyLoadContext, IDisposable
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;
    private IEditorThemeProvider? _provider;

    public EditorThemeProviderLoader(EditorThemeProviderRegistration registration) : base(isCollectible: true)
    {
        _directory = Path.GetDirectoryName(registration.AssemblyPath)!;
        _resolver = new AssemblyDependencyResolver(registration.AssemblyPath);
        try
        {
            using var scope = EnterContextualReflection();
            using FileStream stream = File.OpenRead(registration.AssemblyPath);
            Assembly assembly = LoadFromStream(stream);
            Type type = assembly.GetType(registration.TypeName, throwOnError: true)!;
            if (!typeof(IEditorThemeProvider).IsAssignableFrom(type) || type.IsAbstract)
                throw new InvalidOperationException("The registered type is not an editor theme provider.");
            _provider = (IEditorThemeProvider)Activator.CreateInstance(type)!;
        }
        catch
        {
            InvalidateAssets();
            Unload();
            throw;
        }
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == typeof(IEditorThemeProvider).Assembly.GetName().Name)
            return typeof(IEditorThemeProvider).Assembly;
        if (name.Name is { } shared && (shared == "Avalonia" || shared.StartsWith("Avalonia.", StringComparison.Ordinal)))
            return Default.LoadFromAssemblyName(name);
        string? path = _resolver.ResolveAssemblyToPath(name);
        path ??= Path.Combine(_directory, name.Name + ".dll");
        if (!File.Exists(path)) return null;
        using FileStream stream = File.OpenRead(path);
        return LoadFromStream(stream);
    }

    public IReadOnlyList<EditorTheme> GetThemes()
    {
        using var scope = EnterContextualReflection();
        return (_provider ?? throw new ObjectDisposedException(nameof(EditorThemeProviderLoader))).GetThemes()
            .Select(theme => new EditorTheme(theme.Id, theme.Name, theme.IsDark)).ToArray();
    }

    public EditorThemePalette GetPalette(string themeId)
    {
        InvalidateAssets();
        try
        {
            using var scope = EnterContextualReflection();
            EditorThemePalette palette = (_provider ?? throw new ObjectDisposedException(nameof(EditorThemeProviderLoader))).GetPalette(themeId);
            return new EditorThemePalette(palette.IsDark, new Dictionary<EditorThemeColor, string>(palette.Colors));
        }
        finally { InvalidateAssets(); }
    }

    private void InvalidateAssets()
    {
        foreach (Assembly assembly in Assemblies)
            if (assembly.GetName().Name is { } name) AssetLoader.InvalidateAssemblyCache(name);
    }

    public void Dispose()
    {
        try { (_provider as IDisposable)?.Dispose(); }
        finally
        {
            _provider = null;
            InvalidateAssets();
            Unload();
        }
    }
}
