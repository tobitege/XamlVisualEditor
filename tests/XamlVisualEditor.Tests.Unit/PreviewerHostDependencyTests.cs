using System.Text.Json;
using XamlVisualEditor.Designer.PreviewerHost;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PreviewerHostDependencyTests
{
    [Theory]
    [InlineData("System.Reactive")]
    [InlineData("Microsoft.Extensions.Logging.Abstractions")]
    [InlineData("Microsoft.Extensions.DependencyInjection.Abstractions")]
    public void HostDependencyFileLeavesPackagesToThePreviewApplication(string package)
    {
        string depsPath = Path.ChangeExtension(typeof(Program).Assembly.Location, ".deps.json");
        using JsonDocument deps = JsonDocument.Parse(File.ReadAllText(depsPath));
        string[] libraries = deps.RootElement.GetProperty("targets").EnumerateObject()
            .SelectMany(target => target.Value.EnumerateObject())
            .Concat(deps.RootElement.GetProperty("libraries").EnumerateObject())
            .Select(library => library.Name)
            .ToArray();

        Assert.Contains(libraries, name => name.StartsWith("XamlVisualEditor.Core/", StringComparison.Ordinal));
        Assert.DoesNotContain(libraries, name => name.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase));
    }
}
