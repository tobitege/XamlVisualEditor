using System.Reflection;
using System.Text.Json;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Designer.PreviewerHost;

/// <summary>Forwards control messages from stdin to the UI thread of the host.</summary>
internal static class IncrementalPreviewInput
{
    public static void Register(object builder)
    {
        Type builderType = builder.GetType();
        Action<object> afterSetup = _ => Start();
        Type callbackType = typeof(Action<>).MakeGenericType(builderType);
        Delegate callback = afterSetup.Method.CreateDelegate(callbackType, afterSetup.Target);
        builderType.GetMethod("AfterSetup", new[] { callbackType })!.Invoke(builder, new object[] { callback });
    }

    private static void Start()
    {
        Type dispatcherType = Type.GetType("Avalonia.Threading.Dispatcher, Avalonia.Base", throwOnError: true)!;
        object dispatcher = dispatcherType.GetProperty("UIThread")!.GetValue(null)!;
        MethodInfo post = dispatcherType.GetMethods().Single(method => method.Name == "Post"
            && method.GetParameters() is { Length: 2 } parameters && parameters[0].ParameterType == typeof(Action));
        object priority = Activator.CreateInstance(post.GetParameters()[1].ParameterType)!;
        // Blocking reads stay on a dedicated thread instead of holding a thread-pool thread for the process lifetime.
        Thread reader = new(() => Read(Console.In, action => post.Invoke(dispatcher, new[] { action, priority }),
            RuntimeXamlLoaderRegistrar.ApplyThemeRequest, RuntimeXamlLoaderRegistrar.ApplyIncrementalUpdate, Console.WriteLine))
        {
            IsBackground = true,
            Name = "Preview input"
        };
        reader.Start();
    }

    /// <summary>Dispatches every stdin line until the editor closes the stream.</summary>
    internal static void Read(TextReader input, Action<Action> post,
        Func<DesignerThemeRequest, DesignerThemeResult> applyTheme,
        Func<DesignerUpdateRequest, DesignerUpdateResult> applyUpdate,
        Action<string> write)
    {
        while (input.ReadLine() is { } line)
        {
            switch (Parse(line))
            {
                case DesignerThemeRequest theme:
                    post(() =>
                    {
                        DesignerThemeResult result;
                        try { result = applyTheme(theme); }
                        catch (Exception error) { result = new(theme.RequestId, error.GetBaseException().Message); }
                        write(DesignerThemeProtocol.ResultPrefix + JsonSerializer.Serialize(result));
                    });
                    break;
                case DesignerUpdateRequest update:
                    post(() =>
                    {
                        DesignerUpdateResult result;
                        try { result = applyUpdate(update); }
                        catch (Exception error) { result = new(update.RequestId, false, error.GetBaseException().Message); }
                        write(DesignerUpdateProtocol.ResultPrefix + JsonSerializer.Serialize(result));
                    });
                    break;
            }
        }
    }

    /// <summary>Reads a prefixed theme request or an unprefixed update request; anything else is ignored.</summary>
    internal static object? Parse(string line)
    {
        try
        {
            if (line.StartsWith(DesignerThemeProtocol.RequestPrefix, StringComparison.Ordinal))
                return JsonSerializer.Deserialize<DesignerThemeRequest>(line.AsSpan(DesignerThemeProtocol.RequestPrefix.Length)) is
                    { Providers: not null } theme ? theme : null;
            return JsonSerializer.Deserialize<DesignerUpdateRequest>(line) is { Xaml: not null } update ? update : null;
        }
        catch (JsonException) { return null; }
    }
}
