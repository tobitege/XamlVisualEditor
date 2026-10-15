using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Integration;

public sealed class PreviewerLaunchTests
{

    [Fact]
    public async Task PreviewerLaunchService_StartsPreviewerForAppProject()
    {
        string repoRoot = FindRepoRoot();
        string appProjectPath = Path.Combine(repoRoot, "src", "XamlVisualEditor.App", "XamlVisualEditor.App.csproj");
        string xamlFilePath = Path.Combine(repoRoot, "src", "XamlVisualEditor.App", "MainWindow.axaml");

        await BuildProjectAsync(appProjectPath);

        string? outputAssemblyPath = FindOutputAssembly(Path.GetDirectoryName(appProjectPath)!);
        Assert.False(string.IsNullOrWhiteSpace(outputAssemblyPath));
        Assert.True(File.Exists(outputAssemblyPath!));

        WorkspaceModel workspace = new()
        {
            Projects = new[]
            {
                new ProjectModel
                {
                    Name = "XamlVisualEditor.App",
                    ProjectPath = appProjectPath,
                    XamlFiles = new[]
                    {
                        new XamlFileModel { FilePath = xamlFilePath, RelativePath = "MainWindow.axaml" }
                    },
                    Files = Array.Empty<ProjectFileModel>(),
                    References = Array.Empty<AssemblyReference>(),
                    OutputAssemblyPath = outputAssemblyPath
                }
            },
            ProjectFolders = new System.Collections.Generic.Dictionary<string, string>()
        };

        using PreviewerLaunchService previewerService = new();
        Guid renderId = Guid.NewGuid();
        Guid rootId = Guid.NewGuid();
        Guid buttonId = Guid.NewGuid();
        string xaml = $$"""
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:editor="clr-namespace:XamlVisualEditor.Designer.Rendering;assembly=XamlVisualEditor.Designer.Rendering"
                         Name="PreviewRoot" Width="160" Height="80" Background="{DynamicResource PreviewBackground}"
                         editor:DesignNodeIdentity.NodeId="{{rootId}}" editor:DesignNodeIdentity.RenderId="{{renderId}}">
              <UserControl.Resources>
                <ResourceDictionary>
                  <ResourceDictionary.ThemeDictionaries>
                    <ResourceDictionary x:Key="Light"><SolidColorBrush x:Key="PreviewBackground" Color="#EEF4FA" /></ResourceDictionary>
                    <ResourceDictionary x:Key="Dark"><SolidColorBrush x:Key="PreviewBackground" Color="#102030" /></ResourceDictionary>
                  </ResourceDictionary.ThemeDictionaries>
                </ResourceDictionary>
              </UserControl.Resources>
              <Button Name="PreviewButton" editor:DesignNodeIdentity.NodeId="{{buttonId}}" Content="Host frame" Margin="7,8,9,10" Padding="3,4,5,6" />
            </UserControl>
            """;
        System.Collections.Concurrent.ConcurrentQueue<string> hostMessages = new();
        Task<PreviewerLaunchResult> Start(WorkspaceModel context) => previewerService.StartPreviewerAsync(
            xamlFilePath, xaml, context, null, null, (level, message) => hostMessages.Enqueue(level + ": " + message));
        using Barrier startBarrier = new(2);
        Task<PreviewerLaunchResult> StartTogether() => Task.Run(async () =>
        {
            Assert.True(startBarrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            return await Start(workspace);
        });
        PreviewerLaunchResult[] results = await Task.WhenAll(StartTogether(), StartTogether());
        foreach (PreviewerLaunchResult result in results) Assert.True(result.Success, result.ErrorMessage);
        Process firstProcess = Assert.IsType<Process>(results[0].Process);
        Assert.Same(firstProcess, results[1].Process);

        PreviewerTcpSession firstSession = GetSession(previewerService, xamlFilePath);
        await WaitForFrameAsync(firstSession);
        DesignerHostSnapshot snapshot = await WaitForDesignTreeAsync(firstSession, renderId);
        var rootNode = Assert.Single(snapshot.Nodes, node => node.NodeId == rootId);
        var buttonNode = Assert.Single(snapshot.Nodes, node => node.NodeId == buttonId);
        Assert.Equal(rootNode.InstanceId, buttonNode.ParentInstanceId);
        Assert.Equal("PreviewRoot", rootNode.Name);
        Assert.Equal("PreviewButton", buttonNode.Name);
        Assert.True(buttonNode.Width > 0 && buttonNode.Height > 0);
        Assert.NotNull(buttonNode.Layout);
        Assert.Equal(buttonNode.Width, buttonNode.Layout.Width);
        Assert.Equal(buttonNode.Height, buttonNode.Layout.Height);
        Assert.Equal(7, buttonNode.Layout.MarginLeft);
        Assert.Equal(8, buttonNode.Layout.MarginTop);
        Assert.Equal(9, buttonNode.Layout.MarginRight);
        Assert.Equal(10, buttonNode.Layout.MarginBottom);
        Assert.Equal(3, buttonNode.Layout.PaddingLeft);
        Assert.Equal(4, buttonNode.Layout.PaddingTop);
        Assert.Equal(5, buttonNode.Layout.PaddingRight);
        Assert.Equal(6, buttonNode.Layout.PaddingBottom);
        Assert.Null(buttonNode.Layout.CanvasLeft);
        Assert.Null(buttonNode.Layout.MaxWidth);
        Assert.Equal(1, buttonNode.Layout.GridRowSpan);
        Assert.Equal("Content", rootNode.Layout?.Kind);
        Assert.Equal(160, snapshot.Width);
        Assert.Equal(80, snapshot.Height);
        foreach (string theme in new[] { "Light", "Dark", "Light" })
        {
            string themedXaml = xaml.Replace("Name=\"PreviewRoot\"",
                $"Name=\"PreviewRoot\" editor:DesignPreviewSettings.ThemeVariant=\"{theme}\"", StringComparison.Ordinal);
            (byte red, byte green, byte blue) = theme == "Light"
                ? ((byte)0xEE, (byte)0xF4, (byte)0xFA) : ((byte)0x10, (byte)0x20, (byte)0x30);
            await AssertThemeFrameAsync(firstSession,
                () => previewerService.SendUpdateXamlAsync(xamlFilePath, themedXaml, workspace, null), red, green, blue, hostMessages);
            Assert.Same(firstSession, GetSession(previewerService, xamlFilePath));
            Assert.False(firstProcess.HasExited);
        }
        TypeMetadataService previewMetadata = new();
        previewMetadata.LoadAssembly(typeof(Avalonia.Controls.Control).Assembly.Location);
        previewMetadata.LoadAssembly(typeof(Avalonia.Media.Brush).Assembly.Location);
        previewMetadata.LoadAssembly(typeof(XamlVisualEditor.TestControls.SampleThemeProvider).Assembly.Location);
        XamlVisualEditor.Designer.Rendering.RuntimeDesignRenderer renderer = new(previewMetadata);
        static Guid PreparedRenderId(string prepared) => Guid.Parse(Assert.Single(
            System.Xml.Linq.XDocument.Parse(prepared).Descendants().SelectMany(element => element.Attributes()),
            attribute => attribute.Name.LocalName == "DesignNodeIdentity.RenderId").Value);
        foreach (string rootType in new[] { "Styles", "ResourceDictionary" })
        {
            const string palette = "<ResourceDictionary.ThemeDictionaries><ResourceDictionary x:Key=\"Light\"><SolidColorBrush x:Key=\"PreviewBackground\" Color=\"#EEF4FA\" /></ResourceDictionary><ResourceDictionary x:Key=\"Dark\"><SolidColorBrush x:Key=\"PreviewBackground\" Color=\"#102030\" /></ResourceDictionary></ResourceDictionary.ThemeDictionaries>";
            string definitions = rootType == "Styles"
                ? $"<Styles.Resources><ResourceDictionary>{palette}</ResourceDictionary></Styles.Resources>" : palette;
            string source = $"<{rootType} xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Design.PreviewWith><Border Name=\"PreviewSample\" Width=\"160\" Height=\"80\" Background=\"{{DynamicResource PreviewBackground}}\" /></Design.PreviewWith>{definitions}</{rootType}>";
            var parsed = new XamlVisualEditor.Xaml.Parsing.XamlParsingService().Parse(source);
            var document = Assert.IsType<XamlVisualEditor.Xaml.Ast.MutableAstDocument>(parsed.Document);
            foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
            {
                string prepared = renderer.PrepareXaml(document, themeVariant: theme);
                (byte red, byte green, byte blue) = theme == Avalonia.Styling.ThemeVariant.Light
                    ? ((byte)0xEE, (byte)0xF4, (byte)0xFA) : ((byte)0x10, (byte)0x20, (byte)0x30);
                await AssertThemeFrameAsync(firstSession,
                    () => previewerService.SendUpdateXamlAsync(xamlFilePath, prepared, workspace, null), red, green, blue, hostMessages);
                DesignerHostSnapshot previewTree = await WaitForDesignTreeAsync(firstSession, PreparedRenderId(prepared));
                DesignerHostNode previewNode = Assert.Single(previewTree.Nodes);
                var previewObject = Assert.IsType<XamlVisualEditor.Xaml.Ast.MutableAstObjectNode>(
                    document.Root!.Properties.Single(property => property.PropertyName == "Design.PreviewWith").Value);
                Assert.Equal(previewObject.Id, previewNode.NodeId);
                Assert.Equal("PreviewSample", previewNode.Name);
                Assert.Equal(160, previewNode.Width);
                Assert.Equal(80, previewNode.Height);
                Assert.True(previewNode.IsVisible);
                Assert.Same(firstSession, GetSession(previewerService, xamlFilePath));
                Assert.False(firstProcess.HasExited);
            }
        }
        string packageRoot = Path.Combine(Path.GetTempPath(), "XamlVisualEditor-preview-artifacts", "theme-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        string packagePath = Path.Combine(packageRoot, "sample-themes.nupkg");
        string providerAssembly = typeof(XamlVisualEditor.TestControls.SampleThemeProvider).Assembly.Location;
        string assemblyEntry = "lib/net10.0/" + Path.GetFileName(providerAssembly);
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            using (StreamWriter manifest = new(archive.CreateEntry("xve.extension.json").Open()))
                await manifest.WriteAsync(JsonSerializer.Serialize(new
                {
                    name = "themes", publisher = "sample", version = "1.0.0",
                    contributes = new { designerThemes = new[] { new { id = "palette", name = "Sample", assembly = assemblyEntry,
                        type = typeof(XamlVisualEditor.TestControls.SampleThemeProvider).FullName! } } }
                }));
            await using Stream source = File.OpenRead(providerAssembly);
            await using Stream destination = archive.CreateEntry(assemblyEntry).Open();
            await source.CopyToAsync(destination);
        }
        ExtensionPackageStore packages = new(Path.Combine(packageRoot, "installed"), new ExtensionPackageLoader());
        var installed = await packages.InstallAsync(packagePath, CancellationToken.None);
        DesignerThemeProviderRegistration provider = Assert.Single(DesignerThemePackageContributions.Read(installed));
        Assert.StartsWith(installed.InstallationDirectory!, provider.AssemblyPath, StringComparison.Ordinal);
        const string themedSource = "<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:sample=\"clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls\" Width=\"160\" Height=\"80\"><sample:ThemeSensitiveControl Name=\"ThemeSample\" /></UserControl>";
        var themedDocument = Assert.IsType<XamlVisualEditor.Xaml.Ast.MutableAstDocument>(
            new XamlVisualEditor.Xaml.Parsing.XamlParsingService().Parse(themedSource).Document);
        foreach (string rootType in new[] { "UserControl", "Styles", "ResourceDictionary", "DataTemplate" })
        {
            string previewSource = rootType switch
            {
                "UserControl" => themedSource,
                "DataTemplate" => """
                    <DataTemplate xmlns="https://github.com/avaloniaui"
                                  xmlns:sample="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls">
                      <Design.PreviewWith>
                        <ContentControl Name="TemplatePreview" Width="160" Height="80" DataContext="Preview data">
                          <ContentControl.Template>
                            <ControlTemplate>
                              <ContentPresenter Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" />
                            </ControlTemplate>
                          </ContentControl.Template>
                        </ContentControl>
                      </Design.PreviewWith>
                      <sample:ThemeSensitiveControl Name="ThemeSample" />
                    </DataTemplate>
                    """,
                _ => $"<{rootType} xmlns=\"https://github.com/avaloniaui\" xmlns:sample=\"clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls\"><Design.PreviewWith><sample:ThemeSensitiveControl Name=\"ThemeSample\" Width=\"160\" Height=\"80\" /></Design.PreviewWith></{rootType}>"
            };
            var previewDocument = Assert.IsType<XamlVisualEditor.Xaml.Ast.MutableAstDocument>(
                new XamlVisualEditor.Xaml.Parsing.XamlParsingService().Parse(previewSource).Document);
            string original = new XamlVisualEditor.Xaml.Serialization.XamlSerializationService().Serialize(previewDocument);
            foreach (string? themeId in new string?[] { "harbor", "clay", null })
            {
                DesignerThemeRequest request = new(Guid.NewGuid(), new[] { provider },
                    themeId is null ? null : new DesignerThemeSelection(provider.Id, themeId));
                string prepared = renderer.PrepareXaml(previewDocument, themeRequest: request);
                (byte red, byte green, byte blue) = themeId switch
                {
                    "harbor" => ((byte)0x12, (byte)0x34, (byte)0x56),
                    "clay" => ((byte)0xC0, (byte)0x80, (byte)0x50),
                    _ => ((byte)0x33, (byte)0x44, (byte)0x55)
                };
                await AssertThemeFrameAsync(firstSession,
                    () => previewerService.SendUpdateXamlAsync(xamlFilePath, prepared, workspace, null, themeRequest: request), red, green, blue, hostMessages);
                DesignerHostSnapshot tree = await WaitForDesignTreeAsync(firstSession, PreparedRenderId(prepared));
                DesignerHostNode sample = Assert.Single(tree.Nodes, node => node.Name == "ThemeSample");
                Assert.Equal(160, sample.Width);
                Assert.Equal(80, sample.Height);
                Assert.NotNull(sample.Layout);
                Assert.True(sample.IsVisible);
                DesignerThemeCatalog catalog = Assert.IsType<DesignerThemeCatalog>(firstSession.LastThemeCatalog);
                Assert.Equal(request.RequestId, catalog.RequestId);
                Assert.Equal(new[] { "harbor", "clay" }, Assert.Single(catalog.Providers).Themes.Select(theme => theme.Id));
                Assert.Equal(original, new XamlVisualEditor.Xaml.Serialization.XamlSerializationService().Serialize(previewDocument));
                Assert.Same(firstSession, GetSession(previewerService, xamlFilePath));
                Assert.False(firstProcess.HasExited);
            }
        }
        DesignerThemeProviderRegistration unavailable = provider with
        {
            Id = "missing.provider", Name = "Missing", AssemblyPath = Path.Combine(Path.GetDirectoryName(provider.AssemblyPath)!, "MissingThemeProvider.dll")
        };
        DesignerThemeRequest partialRequest = new(Guid.NewGuid(), new[] { unavailable, provider }, new(provider.Id, "harbor"));
        string partialPreview = renderer.PrepareXaml(themedDocument, themeRequest: partialRequest);
        await AssertThemeFrameAsync(firstSession,
            () => previewerService.SendUpdateXamlAsync(xamlFilePath, partialPreview, workspace, null, themeRequest: partialRequest), 0x12, 0x34, 0x56, hostMessages);
        Assert.NotNull(Assert.Single(firstSession.LastThemeCatalog!.Providers, item => item.Id == unavailable.Id).Error);
        Assert.Null(firstSession.LastError);

        DesignerThemeRequest rejectedRequest = partialRequest with { RequestId = Guid.NewGuid(), Selection = new(unavailable.Id, "saved-theme") };
        TaskCompletionSource<string> rejected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRejected(PreviewerErrorInfo error) => rejected.TrySetResult(error.Message);
        firstSession.ErrorReceived += OnRejected;
        try
        {
            await previewerService.SendUpdateXamlAsync(xamlFilePath, renderer.PrepareXaml(themedDocument, themeRequest: rejectedRequest), workspace, null,
                themeRequest: rejectedRequest);
            Assert.Contains("Missing", await rejected.Task.WaitAsync(TimeSpan.FromSeconds(20)), StringComparison.Ordinal);
        }
        finally { firstSession.ErrorReceived -= OnRejected; }
        DesignerThemeRequest recoveredRequest = partialRequest with { RequestId = Guid.NewGuid(), Selection = null };
        await AssertThemeFrameAsync(firstSession,
            () => previewerService.SendUpdateXamlAsync(xamlFilePath, renderer.PrepareXaml(themedDocument, themeRequest: recoveredRequest), workspace, null,
                themeRequest: recoveredRequest), 0x33, 0x44, 0x55, hostMessages);
        Assert.Null(firstSession.LastError);
        Assert.False(firstProcess.HasExited);
        previewerService.StopPreviewer(xamlFilePath, firstProcess);

        PreviewerLaunchResult restarted = await Start(workspace);
        Assert.True(restarted.Success, restarted.ErrorMessage);
        Process secondProcess = Assert.IsType<Process>(restarted.Process);
        Assert.NotSame(firstProcess, secondProcess);
        PreviewerTcpSession replacement = GetSession(previewerService, xamlFilePath);
        Assert.NotSame(firstSession, replacement);
        await WaitForFrameAsync(replacement);
        Assert.Equal(renderId, (await WaitForDesignTreeAsync(replacement, renderId)).RenderId);
        previewerService.StopPreviewer(xamlFilePath, firstProcess);
        Assert.Same(replacement, GetSession(previewerService, xamlFilePath));
        Assert.False(secondProcess.HasExited);

        WorkspaceModel reloadedWorkspace = new() { Projects = workspace.Projects, ProjectFolders = workspace.ProjectFolders };
        PreviewerLaunchResult reloaded = await Start(reloadedWorkspace);
        Assert.True(reloaded.Success, reloaded.ErrorMessage);
        Process thirdProcess = Assert.IsType<Process>(reloaded.Process);
        Assert.NotSame(secondProcess, thirdProcess);
        PreviewerTcpSession reloadedSession = GetSession(previewerService, xamlFilePath);
        Assert.NotSame(replacement, reloadedSession);
        await WaitForFrameAsync(reloadedSession);
        Assert.Equal(renderId, (await WaitForDesignTreeAsync(reloadedSession, renderId)).RenderId);
        previewerService.StopPreviewer(xamlFilePath, secondProcess);
        Assert.Same(reloadedSession, GetSession(previewerService, xamlFilePath));
        Assert.False(thirdProcess.HasExited);
    }

    private static string FindRepoRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            string candidate = Path.Combine(current, "XamlVisualEditor.slnx");
            if (File.Exists(candidate))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Failed to locate repository root.");
    }

    private static async Task BuildProjectAsync(string projectPath)
    {
        DotNetCliRunner dotNetCli = new();
        DotNetCliResult result = await dotNetCli.RunAsync(
            new[]
            {
                "build",
                projectPath,
                "-c",
                "Debug"
            },
            Path.GetDirectoryName(projectPath));

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"dotnet build failed: {result.StandardError}\n{result.StandardOutput}");
        }
    }

    private static string? FindOutputAssembly(string projectDir)
    {
        string binRoot = Path.Combine(projectDir, "bin", "Debug");
        if (!Directory.Exists(binRoot))
        {
            return null;
        }

        string[] matches = Directory.GetFiles(binRoot, "XamlVisualEditor.App.dll", SearchOption.AllDirectories);
        return matches.FirstOrDefault();
    }

    private static PreviewerTcpSession GetSession(PreviewerLaunchService service, string path)
    {
        Assert.True(service.TryGetSession(path, out PreviewerTcpSession? session));
        return Assert.IsType<PreviewerTcpSession>(session);
    }

    private static async Task<XamlVisualEditor.Shell.ViewModels.DesignerHostSnapshot> WaitForDesignTreeAsync(
        XamlVisualEditor.Shell.ViewModels.PreviewerTcpSession session, Guid renderId)
    {
        TaskCompletionSource<XamlVisualEditor.Shell.ViewModels.DesignerHostSnapshot> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTree(XamlVisualEditor.Shell.ViewModels.DesignerHostSnapshot? tree)
        {
            if (tree?.RenderId == renderId) ready.TrySetResult(tree);
        }
        session.DesignTreeChanged += OnTree;
        try
        {
            OnTree(session.LastDesignTree);
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { session.DesignTreeChanged -= OnTree; }
    }

    private static async Task AssertThemeFrameAsync(PreviewerTcpSession session, Func<Task> update,
        byte red, byte green, byte blue, IEnumerable<string> hostMessages)
    {
        TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(Avalonia.Remote.Protocol.Viewport.FrameMessage frame)
        {
            if (frame.Width < 2 || frame.Height < 2) return;
            int pixel = frame.Stride + 4;
            if (frame.Data.Length > pixel + 3 && frame.Data[pixel] == blue
                && frame.Data[pixel + 1] == green && frame.Data[pixel + 2] == red && frame.Data[pixel + 3] == 255)
                ready.TrySetResult(true);
        }
        session.FrameReceived += OnFrame;
        try
        {
            await update();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException exception)
        {
            var frame = session.LastFrame;
            int pixel = frame is null ? 0 : frame.Stride + 4;
            string actual = frame is not null && frame.Width >= 2 && frame.Height >= 2 && frame.Data.Length > pixel + 3
                ? $"#{frame.Data[pixel + 2]:X2}{frame.Data[pixel + 1]:X2}{frame.Data[pixel]:X2}" : "no frame";
            throw new InvalidOperationException($"Expected theme pixel #{red:X2}{green:X2}{blue:X2}, received {actual}. "
                + $"Previewer error: {session.LastError ?? "<none>"}. Catalog: {session.LastThemeCatalog?.RequestId}." + Environment.NewLine
                + string.Join(Environment.NewLine, hostMessages.TakeLast(30)), exception);
        }
        finally { session.FrameReceived -= OnFrame; }
    }

    private static async Task WaitForFrameAsync(XamlVisualEditor.Shell.ViewModels.PreviewerTcpSession session)
    {
        TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(Avalonia.Remote.Protocol.Viewport.FrameMessage frame) => ready.TrySetResult(true);
        void OnError(PreviewerErrorInfo error) => ready.TrySetException(new InvalidOperationException(error.Message));
        session.FrameReceived += OnFrame;
        session.ErrorReceived += OnError;
        try
        {
            if (session.LastFrame is not null) ready.TrySetResult(true);
            else if (session.LastErrorInfo is { } error) OnError(error);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(session.LastFrame!.Width > 0 && session.LastFrame.Height > 0);
        }
        finally
        {
            session.FrameReceived -= OnFrame;
            session.ErrorReceived -= OnError;
        }
    }

}
