using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class WorkspaceAssemblyResolverTests
{
    [Fact]
    public void Missing_satellite_assemblies_are_not_reported_but_missing_assemblies_are()
    {
        string missingName = "XveMissingProbe" + Guid.NewGuid().ToString("N");
        List<string> messages = new();
        using WorkspaceAssemblyResolver resolver = new([], log: (_, message) => messages.Add(message));

        Assert.ThrowsAny<IOException>(() =>
            AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(missingName + ".resources, Culture=de")));
        Assert.ThrowsAny<IOException>(() =>
            AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(missingName)));

        Assert.Equal(new[] { "Unresolved assembly: " + missingName }, messages);
    }
}
