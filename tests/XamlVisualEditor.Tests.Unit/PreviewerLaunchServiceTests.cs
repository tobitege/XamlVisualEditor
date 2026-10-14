using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using Avalonia;
using XamlVisualEditor.Designer.PreviewerHost;
using SkiaSharp;
using XamlVisualEditor.Core;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Tests.Unit;

[CollectionDefinition(nameof(PreviewerHostEnvironmentCollection), DisableParallelization = true)]
public sealed class PreviewerHostEnvironmentCollection;

[Collection(nameof(PreviewerHostEnvironmentCollection))]
public sealed class PreviewerLaunchServiceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-PreviewerLaunch-" + Guid.NewGuid().ToString("N"));

    public PreviewerLaunchServiceTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task LibraryHostRendersWindowlessControlsWithoutApplicationAssembly(bool useSnapshot)
    {
        Directory.CreateDirectory(_root);
        string librarySource = typeof(XamlVisualEditor.TestControls.CaptionControl).Assembly.Location;
        string outputDirectory = useSnapshot ? Path.Combine(_root, "snapshot") : _root;
        Directory.CreateDirectory(outputDirectory);
        string libraryPath = Path.Combine(outputDirectory, Path.GetFileName(librarySource));
        File.Copy(librarySource, libraryPath);
        using FileStream? liveOutputLock = useSnapshot
            ? new FileStream(Path.Combine(_root, Path.GetFileName(librarySource)), FileMode.Create, FileAccess.ReadWrite, FileShare.None)
            : null;
        string projectPath = Path.Combine(_root, "Library.csproj");
        string xamlPath = Path.Combine(_root, "LibraryView.axaml");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        string xaml = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:lib="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls"
                         x:CompileBindings="False" Width="160" Height="96">
              <Border Background="#102030">
                <lib:CaptionControl Caption="Library" Width="100" Height="40"
                                    HorizontalAlignment="Center" VerticalAlignment="Center">
                  <lib:CaptionControl.Template>
                    <ControlTemplate>
                      <Border Background="#F04020">
                        <TextBlock Text="{ReflectionBinding Caption, RelativeSource={RelativeSource TemplatedParent}}"
                                   Foreground="White" FontSize="18"
                                   HorizontalAlignment="Center" VerticalAlignment="Center" />
                      </Border>
                    </ControlTemplate>
                  </lib:CaptionControl.Template>
                </lib:CaptionControl>
              </Border>
            </UserControl>
            """;
        string originalXaml = xaml;
        XNamespace identity = DesignerUpdateProtocol.IdentityNamespace;
        Guid controlId = Guid.NewGuid();
        Guid revision = Guid.NewGuid();
        XElement preview = XElement.Parse(xaml, LoadOptions.PreserveWhitespace);
        preview.SetAttributeValue(XNamespace.Xmlns + "identity", identity.NamespaceName);
        preview.SetAttributeValue(identity + "DesignNodeIdentity.RenderId", revision);
        XElement caption = preview.Descendants().Single(element => element.Name.LocalName == "CaptionControl");
        caption.SetAttributeValue(identity + "DesignNodeIdentity.NodeId", controlId);
        xaml = preview.ToString(SaveOptions.DisableFormatting);
        File.WriteAllText(xamlPath, xaml);
        WorkspaceModel workspace = new()
        {
            Projects = new[]
            {
                new ProjectModel
                {
                    Name = "Library", ProjectPath = projectPath, OutputAssemblyPath = libraryPath,
                    AssemblySnapshotDirectory = useSnapshot ? outputDirectory : null,
                    TargetFramework = "net10.0", IsExecutable = false,
                    XamlFiles = new[] { new XamlFileModel { FilePath = xamlPath, RelativePath = "LibraryView.axaml" } },
                    Files = Array.Empty<ProjectFileModel>(), References = Array.Empty<AssemblyReference>()
                }
            }
        };
        string? appOverride = Environment.GetEnvironmentVariable("XVE_PREVIEWER_APP_ASSEMBLY");
        try
        {
            Environment.SetEnvironmentVariable("XVE_PREVIEWER_APP_ASSEMBLY", null);
            using PreviewerLaunchService service = new();
            System.Collections.Concurrent.ConcurrentQueue<string> messages = new();
            PreviewerLaunchResult launch = await service.StartPreviewerAsync(xamlPath, xaml, workspace, projectPath,
                runWorkspaceCommandAsync: null, (level, message) => messages.Enqueue($"{level}: {message}"));
            Assert.True(launch.Success, launch.ErrorMessage + Environment.NewLine + string.Join(Environment.NewLine, messages));
            Assert.NotNull(launch.Process);
            using System.Diagnostics.Process lifetime = System.Diagnostics.Process.GetProcessById(launch.Process.Id);
            _output.WriteLine("HOST_PROCESS_ID=" + launch.Process.Id);
            Assert.Contains(libraryPath, launch.Process.StartInfo.Arguments);
            Assert.True(service.TryGetSession(xamlPath, out PreviewerTcpSession? session));
            Assert.NotNull(session);
            session.UpdateViewport(160, 96, 96, 96);
            Avalonia.Remote.Protocol.Viewport.FrameMessage? frame = await MainWindowViewModel.WaitForStableFrameAsync(session, 30000);
            Assert.True(frame is not null, session.LastError + Environment.NewLine + string.Join(Environment.NewLine, messages));
            Assert.Null(session.LastError);
            Assert.Equal(160, frame.Width);
            Assert.Equal(96, frame.Height);
            Assert.Equal(Avalonia.Remote.Protocol.Viewport.PixelFormat.Bgra8888, frame.Format);
            int stride = frame.Stride > 0 ? frame.Stride : frame.Width * 4;
            byte[] Pixel(int x, int y) => frame.Data.AsSpan(y * stride + x * 4, 4).ToArray();
            Assert.Equal(new byte[] { 0x30, 0x20, 0x10, 0xff }, Pixel(4, 4));
            Assert.Equal(new byte[] { 0x20, 0x40, 0xf0, 0xff }, Pixel(32, 30));
            Assert.Contains(Enumerable.Range(0, frame.Width * frame.Height), index =>
            {
                byte[] pixel = Pixel(index % frame.Width, index / frame.Width);
                return pixel[0] > 160 && pixel[1] > 160 && pixel[2] > 160 && pixel[3] > 200;
            });

            async Task UpdateAndWaitForTree(string? width, string? margin)
            {
                revision = Guid.NewGuid();
                preview.SetAttributeValue(identity + "DesignNodeIdentity.RenderId", revision);
                caption.SetAttributeValue("Width", width);
                caption.SetAttributeValue("Margin", margin);
                await service.SendUpdateXamlAsync(xamlPath, preview.ToString(SaveOptions.DisableFormatting),
                    workspace, log: null, workspacePath: projectPath).WaitAsync(TimeSpan.FromSeconds(20));
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
                while (session.LastDesignTree?.RenderId != revision) await Task.Delay(20, timeout.Token);
                Assert.Null(session.LastError);
                Assert.False(lifetime.HasExited);
            }
            using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20)))
                while (session.LastDesignTree?.RenderId != revision) await Task.Delay(20, timeout.Token);
            await UpdateAndWaitForTree("80", "4");
            Assert.Equal(80, Assert.Single(session.LastDesignTree!.Nodes).Width);
            await UpdateAndWaitForTree("100", null);
            Assert.Equal(100, Assert.Single(session.LastDesignTree!.Nodes).Width);
            Assert.Equal(2, messages.Count(message => message.Contains("controls updated without rebuilding", StringComparison.Ordinal)));
            xaml = preview.ToString(SaveOptions.DisableFormatting);

            if (useSnapshot)
            {
                string invalidXaml = originalXaml.Replace("lib:CaptionControl", "lib:MissingControl", StringComparison.Ordinal);
                TaskCompletionSource<PreviewerErrorInfo> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnError(PreviewerErrorInfo error) => failed.TrySetResult(error);
                session.ErrorReceived += OnError;
                PreviewerErrorInfo error;
                try
                {
                    await service.SendUpdateXamlAsync(xamlPath, invalidXaml, workspace, log: null, workspacePath: projectPath);
                    error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }
                finally { session.ErrorReceived -= OnError; }
                Assert.Contains("MissingControl", error.Message);
                Assert.DoesNotContain("TargetInvocationException", error.Message);
                Assert.NotNull(error.ElementPath);
                Assert.Equal("MissingControl", error.ElementPath[^1].Name);
                Assert.Equal(6, DesignerDocumentViewModel.ResolvePreviewerErrorPosition(error, invalidXaml).Line);
                Assert.NotNull(session.LastFrame);

                TaskCompletionSource<bool> recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnErrorChanged(PreviewerErrorInfo? current) { if (current is null) recovered.TrySetResult(true); }
                session.ErrorChanged += OnErrorChanged;
                try
                {
                    await service.SendUpdateXamlAsync(xamlPath, xaml, workspace, log: null, workspacePath: projectPath);
                    await recovered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }
                finally { session.ErrorChanged -= OnErrorChanged; }
                Assert.Null(session.LastErrorInfo);
                Assert.True(service.TryGetSession(xamlPath, out PreviewerTcpSession? recoveredSession));
                Assert.Same(session, recoveredSession);
                Assert.False(lifetime.HasExited);
            }

            string artifactDirectory = Path.Combine(Path.GetTempPath(), "XamlVisualEditor-preview-artifacts");
            Directory.CreateDirectory(artifactDirectory);
            string pngPath = Path.Combine(artifactDirectory, "library-preview-" + Guid.NewGuid().ToString("N") + ".png");
            using SKBitmap bitmap = new(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            for (int row = 0; row < frame.Height; row++)
                System.Runtime.InteropServices.Marshal.Copy(frame.Data, row * stride,
                    bitmap.GetPixels() + row * bitmap.RowBytes, frame.Width * 4);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            using FileStream stream = File.Create(pngPath);
            png.SaveTo(stream);
            _output.WriteLine("PREVIEW_ARTIFACT=" + pngPath);
            service.Dispose();
            Assert.True(lifetime.HasExited, "Disposing the previewer service must release its process before returning.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XVE_PREVIEWER_APP_ASSEMBLY", appOverride);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task FailedStartupPreservesTheOriginalReason()
    {
        using PreviewerLaunchService service = new();
        PreviewerLaunchResult result = await service.StartPreviewerAsync("Missing.axaml", "<UserControl />",
            new WorkspaceModel { Projects = Array.Empty<ProjectModel>() }, null, null, null);
        Assert.False(result.Success);
        Assert.Contains(UiStrings.NoProjectForXaml, result.ErrorMessage);
        Assert.Null(result.Process);
    }

    [Fact]
    public void StructuredPreviewErrorMapsRepeatedElementsBackToOriginalSource()
    {
        const string source = "<UserControl xmlns='https://github.com/avaloniaui'>\n"
            + "  <!-- Original formatting remains unchanged. -->\n"
            + "  <StackPanel>\n"
            + "    <Button Content='First' />\n"
            + "    <!-- The second button fails. -->\n"
            + "    <Button Width='invalid' />\n"
            + "  </StackPanel>\n</UserControl>";
        const string preview = "<UserControl xmlns='https://github.com/avaloniaui'><StackPanel>"
            + "<Button Content='First' /><Button Width='invalid' /></StackPanel></UserControl>";
        Avalonia.Remote.Protocol.Designer.UpdateXamlResultMessage result = new()
        {
            Error = "Full exception stack",
            Exception = new Avalonia.Remote.Protocol.Designer.ExceptionDetails
            {
                Message = "Invalid Width Line 1, position "
                    + (preview.LastIndexOf("<Button", StringComparison.Ordinal) + 1) + ".", LineNumber = 1,
                LinePosition = preview.LastIndexOf("<Button", StringComparison.Ordinal) + 1
            }
        };

        PreviewerErrorInfo error = PreviewerTcpSession.CreateXamlError(result, preview, "Form.axaml");

        Assert.Equal("Invalid Width", error.Message);
        Assert.Equal((6, 6), DesignerDocumentViewModel.ResolvePreviewerErrorPosition(error, source));
        Assert.Equal("Form.axaml", error.FilePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task SnapshotWithIncompleteConfiguredHost_FailsWithoutBuildingOrStarting(bool useOverride)
    {
        string snapshot = Path.Combine(_root, "snapshot");
        Directory.CreateDirectory(snapshot);
        string library = CreateFile(snapshot, "Library.dll");
        string incompleteHost = CreateFile(snapshot, "SelectedHost.dll");
        CreateHostApplication(snapshot, "OtherHost");
        string projectPath = Path.Combine(_root, "Library.csproj");
        string xamlPath = Path.Combine(_root, "View.axaml");
        WorkspaceModel workspace = new()
        {
            Projects = new[]
            {
                new ProjectModel
                {
                    Name = "Library", ProjectPath = projectPath, OutputAssemblyPath = library,
                    AssemblySnapshotDirectory = snapshot,
                    PreviewApplicationAssemblyPath = useOverride ? null : incompleteHost,
                    XamlFiles = Array.Empty<XamlFileModel>(), Files = Array.Empty<ProjectFileModel>(),
                    References = Array.Empty<AssemblyReference>()
                }
            }
        };
        string? previousOverride = Environment.GetEnvironmentVariable("XVE_PREVIEWER_APP_ASSEMBLY");
        try
        {
            Environment.SetEnvironmentVariable("XVE_PREVIEWER_APP_ASSEMBLY",
                useOverride ? Path.Combine(_root, "live", "SelectedHost.dll") : null);
            using PreviewerLaunchService service = new();
            bool buildRequested = false;
            PreviewerLaunchResult result = await service.StartPreviewerAsync(xamlPath, "<UserControl />",
                workspace, projectPath, _ =>
                {
                    buildRequested = true;
                    return System.Threading.Tasks.Task.CompletedTask;
                }, null);

            Assert.False(result.Success);
            Assert.Contains(UiStrings.SnapshotRuntimeAssetsMissing, result.ErrorMessage);
            Assert.Null(result.Process);
            Assert.False(buildRequested);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XVE_PREVIEWER_APP_ASSEMBLY", previousOverride);
        }
    }

    [Fact]
    public void LibraryBootstrapUsesProjectApplicationWithoutConstructingIt()
    {
        AssemblyBuilder library = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("PreviewLibrary_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        TypeBuilder application = library.DefineDynamicModule("Library").DefineType(
            "LibraryApplication", TypeAttributes.Public, typeof(Application));
        application.DefineDefaultConstructor(MethodAttributes.Public);
        Type applicationType = application.CreateType()!;

        AppBuilder builder = Assert.IsType<AppBuilder>(LibraryPreviewBootstrap.CreateAppBuilder(library, Assembly.Load));
        Assert.Same(applicationType, builder.ApplicationType);
        Assert.Null(builder.Instance);
        Assert.NotNull(builder.RenderingSubsystemInitializer);
        Assert.NotNull(builder.TextShapingSubsystemInitializer);
    }

    [Fact]
    public void LibraryBootstrapPreservesProjectFactoryConfiguration()
    {
        AssemblyBuilder library = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("PreviewFactory_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        TypeBuilder factory = library.DefineDynamicModule("Library").DefineType("LibraryFactory", TypeAttributes.Public);
        MethodBuilder method = factory.DefineMethod("BuildAvaloniaApp", MethodAttributes.Public | MethodAttributes.Static,
            typeof(AppBuilder), Type.EmptyTypes);
        ILGenerator il = method.GetILGenerator();
        MethodInfo configure = typeof(AppBuilder).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(candidate => candidate.Name == "Configure" && candidate.IsGenericMethodDefinition
                && candidate.GetParameters().Length == 0).MakeGenericMethod(typeof(Application));
        il.Emit(OpCodes.Call, configure);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldstr, "Project renderer");
        il.Emit(OpCodes.Callvirt, typeof(AppBuilder).GetMethod("UseRenderingSubsystem", new[] { typeof(Action), typeof(string) })!);
        il.Emit(OpCodes.Ret);
        factory.CreateType();

        AppBuilder builder = Assert.IsType<AppBuilder>(LibraryPreviewBootstrap.CreateAppBuilder(library, Assembly.Load));
        Assert.Equal("Project renderer", builder.RenderingSubsystemName);
        Assert.Null(builder.Instance);
    }

    [Fact]
    public void LibraryBootstrapConfiguresRenderingAndThemeForPlainControlLibraries()
    {
        AssemblyBuilder library = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("PlainControls_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        library.DefineDynamicModule("Library");
        AppBuilder builder = Assert.IsType<AppBuilder>(LibraryPreviewBootstrap.CreateAppBuilder(library, Assembly.Load));
        Assert.Same(typeof(Application), builder.ApplicationType);
        Assert.NotNull(builder.RenderingSubsystemInitializer);
        Assert.NotNull(builder.TextShapingSubsystemInitializer);
        Assert.True(builder.AfterSetupCallback.GetInvocationList().Length > 1);
        Assert.Null(builder.Instance);
    }

    [Fact]
    public void ResolveHostApplicationPath_UsesSharedOutputParentName()
    {
        string outputDirectory = Path.Combine(_root, "build", "SampleHost", "Debug");
        Directory.CreateDirectory(outputDirectory);
        string xamlAssembly = CreateFile(outputDirectory, "Library.dll");
        string hostAssembly = CreateHostApplication(outputDirectory, "SampleHost");

        string? resolved = PreviewerLaunchService.ResolveHostApplicationPath(
            xamlAssembly,
            Array.Empty<ProjectModel>(),
            appOverride: null);

        Assert.Equal(Path.GetFullPath(hostAssembly), resolved);
    }

    [Fact]
    public void ResolveHostApplicationPath_PrefersExplicitOverride()
    {
        string outputDirectory = Path.Combine(_root, "output");
        Directory.CreateDirectory(outputDirectory);
        string xamlAssembly = CreateFile(outputDirectory, "Library.dll");
        string overrideAssembly = CreateHostApplication(outputDirectory, "ExplicitHost");

        string? resolved = PreviewerLaunchService.ResolveHostApplicationPath(
            xamlAssembly,
            Array.Empty<ProjectModel>(),
            overrideAssembly);

        Assert.Equal(Path.GetFullPath(overrideAssembly), resolved);
    }

    [Fact]
    public void ResolveHostApplicationPath_PreservesCaseDistinctCandidatesOnCaseSensitiveSystems()
    {
        if (!FileSystemCasing.IsCaseSensitive(_root))
        {
            return;
        }

        string outputDirectory = Path.Combine(_root, "output");
        Directory.CreateDirectory(outputDirectory);
        string xamlAssembly = CreateFile(outputDirectory, "Library.dll");
        CreateHostApplication(outputDirectory, "SampleHost");
        CreateHostApplication(outputDirectory, "samplehost");

        string? resolved = PreviewerLaunchService.ResolveHostApplicationPath(
            xamlAssembly,
            Array.Empty<ProjectModel>(),
            appOverride: null);

        Assert.Null(resolved);
    }

    [Fact]
    public async System.Threading.Tasks.Task PreviewerTcpSession_ReportErrorPreservesVisibleError()
    {
        using PreviewerTcpSession session = new("Sample.axaml", log: null);
        PreviewerErrorInfo? received = null;
        session.ErrorReceived += error => received = error;

        session.ReportError("Host application has no entry point.");

        Assert.Equal("Host application has no entry point.", session.LastError);
        Assert.Equal("Host application has no entry point.", received?.Message);

        const string cause = "System.IO.FileNotFoundException: Could not load file or assembly 'Missing.Controls'.";
        session.ReportStandardErrorLine("Unhandled exception. System.Reflection.TargetInvocationException: Invocation failed.");
        session.ReportStandardErrorLine(" ---> System.Reflection.TargetInvocationException: Nested invocation failed.");
        session.ReportStandardErrorLine(" ---> " + cause);
        PreviewerErrorInfo rootCause = Assert.IsType<PreviewerErrorInfo>(received);
        foreach (string line in new[]
        {
            "File name: 'Missing.Controls'", "Dateiname: 'Missing.Controls'",
            "   at System.Reflection.RuntimeAssembly.InternalLoad(AssemblyName name)",
            "   bei Sample.Application.Initialize()",
            "   --- End of inner exception stack trace ---",
            "--- End of stack trace from previous location ---", " "
        }) session.ReportStandardErrorLine(line);
        Assert.Equal(cause, session.LastError);
        Assert.Same(rootCause, received);

        TaskCompletionSource<bool> recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ErrorChanged += error => { if (error is null) recovered.TrySetResult(true); };
        using Avalonia.Remote.Protocol.IAvaloniaRemoteTransportConnection connection =
            await new Avalonia.Remote.Protocol.BsonTcpTransport().Connect(System.Net.IPAddress.Loopback, session.Port);
        connection.Start();
        await connection.Send(new Avalonia.Remote.Protocol.Designer.UpdateXamlResultMessage());
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(session.LastError);
        session.ReportStandardErrorLine("   at Sample.PreviousRender.Load()");
        Assert.Null(session.LastError);
        session.ReportStandardErrorLine("A different resource could not be loaded.");
        Assert.Equal("A different resource could not be loaded.", session.LastError);
    }

    [Fact]
    public void A_Crash_Keeps_The_Cause_From_The_Error_Output_Instead_Of_The_Broken_Connection()
    {
        using PreviewerTcpSession session = new("Sample.axaml", log: null);

        session.ReportConnectionLost(new IOException("Unable to read data from the transport connection."), connection: null);
        Assert.Equal(UiStrings.PreviewerConnectionLost, session.LastError);

        const string cause = "System.InvalidOperationException: The version of the native libSkiaSharp library (88.1) is incompatible with this version of SkiaSharp.";
        session.ReportStandardErrorLine("Unhandled exception. System.Reflection.TargetInvocationException: Exception has been thrown by the target of an invocation.");
        session.ReportStandardErrorLine(" ---> " + cause);
        session.ReportStandardErrorLine("   at SkiaSharp.SKObject..cctor()");
        session.ReportConnectionLost(new IOException("Unable to read data from the transport connection."), connection: null);

        Assert.Equal(cause, session.LastError);
    }

    [Fact]
    public void MetadataInspectionDoesNotLoadTheProjectAssemblyIntoTheEditor()
    {
        Directory.CreateDirectory(_root);
        string name = "MetadataOnly_" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(_root, name + ".dll");
        PersistedAssemblyBuilder assembly = new(new AssemblyName(name), typeof(object).Assembly);
        assembly.DefineDynamicModule(name);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyMetadataAttribute).GetConstructor(new[] { typeof(string), typeof(string) })!,
            new object[] { "AvaloniaUseCompiledBindingsByDefault", "true" }));
        assembly.Save(path);

        Assert.True(PreviewerLaunchService.TryGetAssemblyMetadataValue(path,
            "AvaloniaUseCompiledBindingsByDefault", out string? value));
        Assert.Equal("true", value);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), loaded => loaded.GetName().Name == name);
        Assert.False(PreviewerLaunchService.TryGetAssemblyMetadataValue(path, "MissingKey", out _));
    }

    [Fact]
    public async System.Threading.Tasks.Task DesignTreesAreAcceptedOnlyForTheCurrentPreviewRevision()
    {
        using PreviewerTcpSession session = new("Sample.axaml", log: null);
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        DesignerHostNode node = new(Guid.NewGuid(), 1, null, "Avalonia.Controls.Button", "Action", 0, 0, 100, 30, true);
        DesignerHostSnapshot snapshot = new(first, 100, 30, new[] { node });
        string Message(DesignerHostSnapshot tree) => PreviewerTcpSession.DesignTreeMessagePrefix + System.Text.Json.JsonSerializer.Serialize(tree);
        await session.SendUpdateXamlAsync("<Button />", "Library.dll", "/Sample.axaml", null, null, first);
        Assert.True(session.TryReceiveDesignTree(Message(snapshot)));
        Assert.Equal(first, session.LastDesignTree?.RenderId);
        await session.SendUpdateXamlAsync("<Button />", "Library.dll", "/Sample.axaml", null, null, second);
        Assert.Null(session.LastDesignTree);
        Assert.True(session.TryReceiveDesignTree(Message(snapshot)));
        Assert.Null(session.LastDesignTree);
        Assert.True(session.TryReceiveDesignTree(Message(snapshot with { RenderId = second })));
        Assert.Equal(second, session.LastDesignTree?.RenderId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string CreateHostApplication(string directory, string baseName)
    {
        string assemblyPath = CreateFile(directory, baseName + ".dll");
        CreateFile(directory, baseName + ".runtimeconfig.json");
        CreateFile(directory, baseName + ".deps.json");
        return assemblyPath;
    }

    private static string CreateFile(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }
}
