using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using XamlVisualEditor.CSharp.Language;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class CSharpWorkspaceManagerTests
{
    [Fact]
    public async Task InitializeWorkspaceAsync_DefersMsbuildLoadUntilDocumentRequest()
    {
        RecordingLogger<CSharpWorkspaceManager> logger = new();
        using CSharpWorkspaceManager manager = new(logger);
        string missingProject = Path.Combine(
            Path.GetTempPath(),
            "XamlVisualEditor-Missing-" + Guid.NewGuid().ToString("N"),
            "Missing.csproj");

        await manager.InitializeWorkspaceAsync(missingProject, CancellationToken.None);

        Assert.Empty(logger.Messages);

        Document? document = await manager.GetOrAddDocumentAsync(
            Path.ChangeExtension(missingProject, ".cs"),
            "internal sealed class Sample { }",
            CancellationToken.None);

        Assert.NotNull(document);
        Assert.Contains(logger.Messages, message => message.Contains("Failed to load MSBuild workspace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetOrAddDocumentAsync_KeepsProjectDocumentTextInMemory()
    {
        string root = Path.Combine(Path.GetTempPath(), "XamlVisualEditor-InMemory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string projectPath = Path.Combine(root, "Sample.csproj");
        string sourcePath = Path.Combine(root, "Sample.cs");
        const string diskText = "internal sealed class Sample { }";
        const string bufferText = "internal sealed class Sample { private int _value; }";
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(sourcePath, diskText);
        try
        {
            // The application registers MSBuild when it loads a workspace; the test process has to do it itself.
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            RecordingLogger<CSharpWorkspaceManager> logger = new();
            using CSharpWorkspaceManager manager = new(logger);
            await manager.InitializeWorkspaceAsync(projectPath, CancellationToken.None);

            Document? document = await manager.GetOrAddDocumentAsync(sourcePath, bufferText, CancellationToken.None);

            Assert.NotNull(document);
            Assert.True(manager.IsProjectDocument(document), string.Join(Environment.NewLine, logger.Messages));
            Assert.Equal(bufferText, (await document.GetTextAsync()).ToString());
            Assert.Equal(diskText, File.ReadAllText(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
