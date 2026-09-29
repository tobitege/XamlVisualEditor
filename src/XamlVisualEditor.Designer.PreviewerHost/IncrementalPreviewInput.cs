using System.Reflection;
using System.Text.Json;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Designer.PreviewerHost;

/// <summary>Forwards local update requests from stdin to the UI thread of the host.</summary>
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
        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                DesignerUpdateRequest? request;
                try { request = JsonSerializer.Deserialize<DesignerUpdateRequest>(line); }
                catch (JsonException) { continue; }
                if (request is null) continue;
                Action apply = () =>
                {
                    DesignerUpdateResult result;
                    try { result = RuntimeXamlLoaderRegistrar.ApplyIncrementalUpdate(request); }
                    catch (Exception error) { result = new(request.RequestId, false, error.GetBaseException().Message); }
                    Console.WriteLine(DesignerUpdateProtocol.ResultPrefix + JsonSerializer.Serialize(result));
                };
                post.Invoke(dispatcher, new[] { apply, priority });
            }
        });
    }
}
