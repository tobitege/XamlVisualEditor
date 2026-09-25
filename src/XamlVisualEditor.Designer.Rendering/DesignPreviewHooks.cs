using Avalonia.Controls;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Designer.Rendering;

/// <summary>
/// Extension point for the design surface. Hooks let external extensions refine the
/// rendered preview of control libraries whose runtime environment the design surface
/// does not provide (for example containers that wrap their content at run time).
/// </summary>
public interface IDesignPreviewHook
{
    /// <summary>
    /// Called on the UI thread after the design surface rendered a document and before it
    /// is shown. <paramref name="root"/> is the rendered root control (the actual
    /// <see cref="Window"/> for window documents); <paramref name="rootNode"/> is the
    /// document's source root element, including directives such as x:Class.
    /// Hooks may adjust the rendered tree in place.
    /// </summary>
    void PostProcess(Control root, MutableAstObjectNode rootNode);
}

/// <summary>
/// Process-wide registry for design preview hooks. A registry (instead of constructor
/// injection) because the design surface renders per document while hooks arrive from
/// independently activated extensions.
/// </summary>
public static class DesignPreviewHookRegistry
{
    private static readonly Lock Gate = new();
    private static IDesignPreviewHook[] _hooks = [];

    /// <summary>Gets the currently registered hooks.</summary>
    public static IReadOnlyList<IDesignPreviewHook> Hooks => _hooks;

    /// <summary>Registers a hook; disposing the result removes it again.</summary>
    public static IDisposable Register(IDesignPreviewHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        lock (Gate)
        {
            _hooks = [.. _hooks, hook];
        }

        return new Registration(hook);
    }

    /// <summary>
    /// Runs every registered hook on a rendered document. A failing hook does not stop the
    /// others or the preview; <paramref name="onError"/> receives the failure.
    /// </summary>
    public static void Apply(Control root, MutableAstObjectNode rootNode, Action<IDesignPreviewHook, Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(rootNode);
        ArgumentNullException.ThrowIfNull(onError);
        foreach (IDesignPreviewHook hook in _hooks)
        {
            try
            {
                hook.PostProcess(root, rootNode);
            }
            catch (Exception exception)
            {
                onError(hook, exception);
            }
        }
    }

    private static void Remove(IDesignPreviewHook hook)
    {
        lock (Gate)
        {
            _hooks = _hooks.Where(existing => !ReferenceEquals(existing, hook)).ToArray();
        }
    }

    private sealed class Registration(IDesignPreviewHook hook) : IDisposable
    {
        private IDesignPreviewHook? _hook = hook;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _hook, null) is { } registered)
            {
                Remove(registered);
            }
        }
    }
}
