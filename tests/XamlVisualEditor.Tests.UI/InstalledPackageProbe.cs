using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlVisualEditor.Extensions;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

/// <summary>
/// Builds real extension packages whose main assembly is compiled on the fly, and records
/// their activation. Each package gets a unique assembly name because the host loads main
/// assemblies into the default load context.
/// </summary>
public static class InstalledPackageProbe
{
    private static readonly ConcurrentDictionary<string, string> Activations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called by the compiled probe extensions during activation.</summary>
    public static void Record(string extensionId, string extensionPath) => Activations[extensionId] = extensionPath;

    public static string? GetActivationPath(string extensionId) =>
        Activations.TryGetValue(extensionId, out string? path) ? path : null;

    public static string CreatePackage(string root, string name)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string assemblyName = "XveProbe" + suffix;
        string buildDirectory = Path.Combine(root, "build-" + suffix);
        Directory.CreateDirectory(buildDirectory);
        string assemblyPath = Path.Combine(buildDirectory, assemblyName + ".dll");
        Compile(assemblyName, assemblyPath);

        string manifest = $$"""
            { "name": "{{name}}{{suffix}}", "publisher": "probe", "version": "1.0.0", "main": "{{assemblyName}}.dll" }
            """;
        string packagePath = Path.Combine(root, $"{name}-{suffix}.nupkg");
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        using (StreamWriter writer = new(archive.CreateEntry("xve.extension.json").Open()))
        {
            writer.Write(manifest);
        }

        archive.CreateEntryFromFile(assemblyPath, assemblyName + ".dll");
        return packagePath;
    }

    public static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Loaded main assemblies stay locked until the test process exits.
        }
    }

    private static void Compile(string assemblyName, string assemblyPath)
    {
        const string source = """
            public sealed class ProbeExtension : XamlVisualEditor.Extensions.IXveExtension
            {
                public System.Threading.Tasks.Task ActivateAsync(
                    XamlVisualEditor.Extensions.ExtensionContext context, System.Threading.CancellationToken cancellationToken)
                {
                    XamlVisualEditor.Tests.UI.InstalledPackageProbe.Record(context.ExtensionId, context.ExtensionPath);
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """;
        MetadataReference[] references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(IXveExtension).Assembly.Location)
            .Append(typeof(InstalledPackageProbe).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(assemblyPath);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }
}
