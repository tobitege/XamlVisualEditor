using System.Text.Json;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.PreviewerHost;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PreviewerHostInputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xve-host-input-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DocumentNamingAThemeProviderLoadsNothing()
    {
        Directory.CreateDirectory(_root);
        string providerPath = Path.Combine(_root, "DocumentThemeProvider.dll");
        File.Copy(typeof(XamlVisualEditor.TestControls.SampleThemeProvider).Assembly.Location, providerPath);
        DesignerThemeRequest documentRequest = Request(providerPath);
        string xaml = $"<Canvas xmlns='https://github.com/avaloniaui' xmlns:preview='{DesignerThemeProtocol.XmlNamespace}' "
            + $"preview:{DesignerThemeProtocol.PropertyName}='{{}}{JsonSerializer.Serialize(documentRequest).Replace("'", "&apos;")}' />";
        DesignerUpdateRequest update = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), xaml);
        List<DesignerThemeRequest> themes = new();
        List<string> output = new();

        IncrementalPreviewInput.Read(new StringReader(JsonSerializer.Serialize(update)), action => action(),
            request => { themes.Add(request); return new(request.RequestId); },
            RuntimeXamlLoaderRegistrar.ApplyIncrementalUpdate, output.Add);

        Assert.Empty(themes);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(),
            assembly => !assembly.IsDynamic && string.Equals(assembly.Location, providerPath, StringComparison.OrdinalIgnoreCase));
        DesignerUpdateResult result = Assert.IsType<DesignerUpdateResult>(JsonSerializer.Deserialize<DesignerUpdateResult>(
            Assert.Single(output)[DesignerUpdateProtocol.ResultPrefix.Length..]));
        Assert.Equal(update.RequestId, result.RequestId);
        Assert.False(result.Applied);
    }

    [Fact]
    public void ThemeRequestOnStandardInputIsAppliedAndConfirmed()
    {
        DesignerThemeRequest request = Request(typeof(XamlVisualEditor.TestControls.SampleThemeProvider).Assembly.Location);
        List<DesignerThemeRequest> themes = new();
        List<string> output = new();
        List<Action> posted = new();

        IncrementalPreviewInput.Read(new StringReader(DesignerThemeProtocol.RequestPrefix + JsonSerializer.Serialize(request)),
            posted.Add, theme => { themes.Add(theme); return new(theme.RequestId, "rejected"); },
            _ => throw new InvalidOperationException("No update was sent."), output.Add);

        Assert.Empty(themes);
        Assert.Single(posted)();
        DesignerThemeRequest applied = Assert.Single(themes);
        Assert.Equal(request.RequestId, applied.RequestId);
        Assert.Equal(request.Selection, applied.Selection);
        Assert.Equal(request.Providers, applied.Providers);
        string line = Assert.Single(output);
        Assert.StartsWith(DesignerThemeProtocol.ResultPrefix, line, StringComparison.Ordinal);
        Assert.Equal(new DesignerThemeResult(request.RequestId, "rejected"),
            JsonSerializer.Deserialize<DesignerThemeResult>(line[DesignerThemeProtocol.ResultPrefix.Length..]));
    }

    [Fact]
    public void FailingThemeProviderIsReportedInTheConfirmation()
    {
        DesignerThemeRequest request = Request(typeof(XamlVisualEditor.TestControls.SampleThemeProvider).Assembly.Location);
        List<string> output = new();

        IncrementalPreviewInput.Read(new StringReader(DesignerThemeProtocol.RequestPrefix + JsonSerializer.Serialize(request)),
            action => action(), _ => throw new InvalidOperationException("Provider failed"),
            _ => throw new InvalidOperationException("No update was sent."), output.Add);

        Assert.Equal(new DesignerThemeResult(request.RequestId, "Provider failed"),
            JsonSerializer.Deserialize<DesignerThemeResult>(Assert.Single(output)[DesignerThemeProtocol.ResultPrefix.Length..]));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("XVE-THEME-REQUEST {")]
    [InlineData("XVE-THEME-REQUEST {\"RequestId\":\"6f1c2b9e-1f0a-4a55-9a76-3c1f6a0d9b11\"}")]
    [InlineData("{\"RequestId\":\"6f1c2b9e-1f0a-4a55-9a76-3c1f6a0d9b11\",\"Providers\":[],\"Selection\":null}")]
    public void UnrecognizedLinesAreIgnored(string line)
    {
        Assert.Null(IncrementalPreviewInput.Parse(line));
    }

    private static DesignerThemeRequest Request(string assemblyPath)
    {
        DesignerThemeProviderRegistration provider = new("sample.palette", "Sample", assemblyPath,
            typeof(XamlVisualEditor.TestControls.SampleThemeProvider).FullName!);
        return new(Guid.NewGuid(), new[] { provider }, new DesignerThemeSelection(provider.Id, "harbor"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
