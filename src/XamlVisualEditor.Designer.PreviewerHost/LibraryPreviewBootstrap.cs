using System;
using System.Linq;
using System.Reflection;

namespace XamlVisualEditor.Designer.PreviewerHost;

internal static class LibraryPreviewBootstrap
{
    private const BindingFlags FactoryFlags = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Static | BindingFlags.FlattenHierarchy;

    public static object CreateAppBuilder(Assembly target, Func<string, Assembly> loadAssembly)
    {
        Assembly controls = loadAssembly("Avalonia.Controls");
        Type builderType = controls.GetType("Avalonia.AppBuilder", throwOnError: true)!;
        Type applicationType = controls.GetType("Avalonia.Application", throwOnError: true)!;
        MethodInfo? entryFactory = FindFactory(target.EntryPoint?.DeclaringType);
        if (entryFactory is not null) return InvokeFactory(entryFactory, builderType);

        Type[] types = target.GetTypes();
        MethodInfo[] factories = types.Select(FindFactory).OfType<MethodInfo>().Distinct().ToArray();
        if (factories.Length > 1)
            throw new InvalidOperationException("Multiple BuildAvaloniaApp methods were found. Configure a previewer application explicitly.");
        if (factories.Length == 1) return InvokeFactory(factories[0], builderType);

        Type[] applications = types.Where(type => applicationType.IsAssignableFrom(type)
            && !type.IsAbstract && !type.ContainsGenericParameters).ToArray();
        if (applications.Length > 1)
            throw new InvalidOperationException("Multiple Avalonia applications were found. Configure a previewer application explicitly.");
        Type selectedApplication = applications.SingleOrDefault() ?? applicationType;
        if (selectedApplication.GetConstructor(Type.EmptyTypes) is null)
            throw new InvalidOperationException($"{selectedApplication.FullName} requires a public parameterless constructor or BuildAvaloniaApp method.");

        MethodInfo configure = builderType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "Configure" && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 0);
        object builder = configure.MakeGenericMethod(selectedApplication).Invoke(null, null)!;
        Type desktop = loadAssembly("Avalonia.Desktop").GetType("Avalonia.AppBuilderDesktopExtensions", throwOnError: true)!;
        desktop.GetMethod("UsePlatformDetect", new[] { builderType })!.Invoke(null, new[] { builder });

        if (selectedApplication == applicationType)
        {
            Action<object> addTheme = configured =>
            {
                object application = builderType.GetProperty("Instance")!.GetValue(configured)!;
                object styles = applicationType.GetProperty("Styles")!.GetValue(application)!;
                Type themeType = loadAssembly("Avalonia.Themes.Fluent")
                    .GetType("Avalonia.Themes.Fluent.FluentTheme", throwOnError: true)!;
                object theme = Activator.CreateInstance(themeType, new object?[] { null })!;
                styles.GetType().GetMethod("Add")!.Invoke(styles, new[] { theme });
            };
            Type callbackType = typeof(Action<>).MakeGenericType(builderType);
            Delegate callback = addTheme.Method.CreateDelegate(callbackType, addTheme.Target);
            builderType.GetMethod("AfterSetup", new[] { callbackType })!.Invoke(builder, new object[] { callback });
        }

        return builder;
    }

    private static MethodInfo? FindFactory(Type? type)
    {
        MethodInfo? method = type?.GetMethod("BuildAvaloniaApp", FactoryFlags, null, Type.EmptyTypes, null);
        return method?.ContainsGenericParameters == false ? method : null;
    }

    private static object InvokeFactory(MethodInfo factory, Type builderType)
    {
        object? builder = factory.Invoke(null, null);
        return builder is not null && builderType.IsInstanceOfType(builder) ? builder
            : throw new InvalidOperationException($"{factory.DeclaringType?.FullName}.BuildAvaloniaApp must return an Avalonia AppBuilder.");
    }
}
