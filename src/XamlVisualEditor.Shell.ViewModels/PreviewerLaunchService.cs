using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

internal sealed class PreviewerLaunchService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PreviewerInstance> _instances = new(FileSystemPathComparison.Comparer);
    private readonly PreviewerTelemetryStore _telemetry = new();
    private bool _disposed;

    private sealed class PreviewerInstance
    {
        public required WorkspaceModel Workspace { get; init; }
        public string? WorkspacePath { get; init; }
        public Action<string, string>? Log { get; init; }
        public TaskCompletionSource<PreviewerLaunchResult> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Process? Process { get; set; }
        public PreviewerTcpSession? Session { get; set; }
        public string? XamlText { get; set; }
        public DesignerThemeRequest? ThemeRequest { get; set; }
        public SemaphoreSlim UpdateGate { get; } = new(1, 1);
        public Guid? LastSentRenderId { get; set; }
        public Guid? LastSentThemeRequestId { get; set; }
        public Guid PendingUpdateId { get; set; }
        public TaskCompletionSource<bool>? PendingUpdate { get; set; }
        public Guid PendingThemeId { get; set; }
        public TaskCompletionSource<DesignerThemeResult>? PendingTheme { get; set; }
        /// <summary>The host that took over after a theme change; set before <see cref="Started"/> completes.</summary>
        public PreviewerInstance? ReplacedBy { get; set; }
    }

    public event Action<PreviewerErrorInfo>? PreviewerErrorReceived;

    /// <summary>
    /// Raised on a process thread with the file path when the current host of a file exits without being stopped; its
    /// instance is already released, so <see cref="TryGetSession"/> no longer returns its session.
    /// </summary>
    public event Action<string>? PreviewerExited;

    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Waits for an ended host process; returns false when it did not exit within the timeout.</summary>
    internal Func<Process, TimeSpan, bool> WaitForProcessExit { get; init; } =
        static (process, timeout) => process.WaitForExit(timeout);

    /// <summary>Registers an already running host process and its session for a file; the exit of the process is reported like that of a started host.</summary>
    internal void AddRunningInstance(string xamlFilePath, WorkspaceModel workspace, string? workspacePath,
        Process? process, PreviewerTcpSession? session, Action<string, string>? log = null, Guid? appliedThemeRequestId = null)
    {
        PreviewerInstance instance = new()
        {
            Workspace = workspace, WorkspacePath = workspacePath, Log = log, Process = process, Session = session,
            LastSentThemeRequestId = appliedThemeRequestId
        };
        instance.Started.TrySetResult(new PreviewerLaunchResult(true, null, process));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _instances.Add(xamlFilePath, instance);
        }
        if (process is null) return;
        process.Exited += (_, _) => OnProcessExited(xamlFilePath, instance, log);
        process.EnableRaisingEvents = true;
    }

    /// <summary>Gets the XAML and theme request that the host of a file renders; both are null without a host.</summary>
    internal (string? Xaml, DesignerThemeRequest? ThemeRequest) GetRequestedContent(string xamlFilePath)
    {
        lock (_gate)
        {
            return _instances.TryGetValue(xamlFilePath, out PreviewerInstance? instance)
                ? (instance.XamlText, instance.ThemeRequest)
                : (null, null);
        }
    }

    public async Task<PreviewerLaunchResult> StartPreviewerAsync(
        string xamlFilePath,
        string? xamlText,
        WorkspaceModel workspace,
        string? workspacePath,
        Func<string, Task>? runWorkspaceCommandAsync,
        Action<string, string>? log,
        DesignerThemeRequest? themeRequest = null,
        bool fullRender = false)
    {
        PreviewerInstance instance;
        PreviewerInstance? replaced = null;
        bool created;
        lock (_gate)
        {
            if (_disposed) return PreviewerLaunchResult.Fail("The previewer service is closed.");
            created = !_instances.TryGetValue(xamlFilePath, out instance!);
            if (!created && (!ReferenceEquals(instance.Workspace, workspace) ||
                !FileSystemPathComparison.Comparer.Equals(instance.WorkspacePath, workspacePath)))
            {
                replaced = instance;
                _instances.Remove(xamlFilePath);
                created = true;
            }
            if (created)
            {
                instance = new PreviewerInstance { Workspace = workspace, WorkspacePath = workspacePath, Log = log };
                _instances.Add(xamlFilePath, instance);
            }
            if (!string.IsNullOrWhiteSpace(xamlText))
            {
                instance.XamlText = xamlText;
                instance.ThemeRequest = themeRequest;
            }
        }

        if (replaced is not null)
        {
            replaced.Started.TrySetResult(PreviewerLaunchResult.Fail("The previewer workspace changed."));
            DisposeInstance(replaced);
        }

        if (created)
            _ = StartInstanceAsync(xamlFilePath, instance, workspace, workspacePath, runWorkspaceCommandAsync, log);

        (instance, PreviewerLaunchResult result) = await WaitForStartAsync(instance);
        if (result.Success && !IsCurrent(xamlFilePath, instance))
            return PreviewerLaunchResult.Fail("The previewer was closed or replaced.");
        if (!created && result.Success && !string.IsNullOrWhiteSpace(xamlText))
            return await UpdateInstanceAsync(xamlFilePath, xamlText, workspace, log, workspacePath, themeRequest,
                latestTextOnly: true, fullRender);
        return result;
    }

    // A theme change replaces a host; whoever waits for the replaced host follows its replacement.
    private static async Task<(PreviewerInstance Instance, PreviewerLaunchResult Result)> WaitForStartAsync(PreviewerInstance instance)
    {
        PreviewerLaunchResult result = await instance.Started.Task;
        while (instance.ReplacedBy is { } replacement)
        {
            instance = replacement;
            result = await instance.Started.Task;
        }
        return (instance, result);
    }

    private async Task StartInstanceAsync(
        string xamlFilePath,
        PreviewerInstance instance,
        WorkspaceModel workspace,
        string? workspacePath,
        Func<string, Task>? runWorkspaceCommandAsync,
        Action<string, string>? log)
    {
        try
        {
        if (!IsCurrent(xamlFilePath, instance)) return;
        if (!TryCreateLaunchInfo(xamlFilePath, workspace, workspacePath, out PreviewerLaunchInfo launchInfo, out string error))
            throw new InvalidOperationException(error);

        if (!launchInfo.HasRequiredAssets)
        {
            if (runWorkspaceCommandAsync is not null && !string.IsNullOrWhiteSpace(workspacePath))
            {
                log?.Invoke("Info", "Previewer assets missing. Building workspace...");
                await runWorkspaceCommandAsync("build");
            }

            if (!TryCreateLaunchInfo(xamlFilePath, workspace, workspacePath, out launchInfo, out error))
                throw new InvalidOperationException(error);
            if (!launchInfo.HasRequiredAssets)
                throw new InvalidOperationException("Previewer assets are still missing. Build the workspace and editor first.");
        }

        if (!IsCurrent(xamlFilePath, instance)) return;
        LogCompiledBindingsSetting(launchInfo.TargetAssemblyPath, log);
        Stopwatch stopwatch = Stopwatch.StartNew();
        Process process;
        lock (_gate)
        {
            if (!IsCurrent(xamlFilePath, instance)) return;
            PreviewerTcpSession session = new(xamlFilePath, log);
            instance.Session = session;
            session.ErrorReceived += errorInfo =>
            {
                if (IsCurrent(xamlFilePath, instance)) PreviewerErrorReceived?.Invoke(errorInfo);
            };
            launchInfo = launchInfo with { TransportUri = $"tcp-bson://127.0.0.1:{session.Port}/" };
            process = new Process { StartInfo = BuildStartInfo(launchInfo), EnableRaisingEvents = true };
            instance.Process = process;
            process.Exited += (_, _) => OnProcessExited(xamlFilePath, instance, log);
            process.OutputDataReceived += (_, e) =>
            {
                if (IsCurrent(xamlFilePath, instance) && !string.IsNullOrWhiteSpace(e.Data))
                {
                    if (!TryReceiveIncrementalResult(instance, e.Data) && !TryReceiveThemeResult(instance, e.Data)
                        && !session.TryReceiveDesignTree(e.Data) && !session.TryReceiveThemeCatalog(e.Data))
                        log?.Invoke("Info", $"Previewer: {e.Data}");
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (IsCurrent(xamlFilePath, instance) && !string.IsNullOrWhiteSpace(e.Data))
                {
                    log?.Invoke("Error", $"Previewer: {e.Data}");
                    session.ReportStandardErrorLine(e.Data);
                }
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        stopwatch.Stop();
        _telemetry.RecordStart(stopwatch.Elapsed);
        string? latestXaml;
        lock (_gate) latestXaml = instance.XamlText;
        if (!string.IsNullOrWhiteSpace(latestXaml))
            await SendInstanceUpdateAsync(xamlFilePath, instance, latestXaml, launchInfo);
        lock (_gate)
        {
            if (IsCurrent(xamlFilePath, instance))
                instance.Started.TrySetResult(PreviewerLaunchResult.FromProcess(process));
        }
        if (IsCurrent(xamlFilePath, instance))
            log?.Invoke("Info", $"Previewer started: {launchInfo.TargetAssemblyPath}");
        }
        catch (Exception ex)
        {
            _telemetry.RecordFailure(ex.Message);
            ReleaseInstance(xamlFilePath, instance, $"Previewer launch failed: {ex.Message}");
        }
    }

    /// <summary>Sends a document to the running host of a file; the result reports a host that failed to restart.</summary>
    public Task<PreviewerLaunchResult> SendUpdateXamlAsync(
        string xamlFilePath,
        string xamlText,
        WorkspaceModel workspace,
        Action<string, string>? log,
        string? workspacePath = null,
        DesignerThemeRequest? themeRequest = null) =>
        UpdateInstanceAsync(xamlFilePath, xamlText, workspace, log, workspacePath, themeRequest,
            latestTextOnly: false, fullRender: false);

    private async Task<PreviewerLaunchResult> UpdateInstanceAsync(
        string xamlFilePath,
        string xamlText,
        WorkspaceModel workspace,
        Action<string, string>? log,
        string? workspacePath,
        DesignerThemeRequest? themeRequest,
        bool latestTextOnly,
        bool fullRender)
    {
        PreviewerInstance? instance;
        PreviewerInstance? replacement = null;
        lock (_gate)
        {
            if (!_instances.TryGetValue(xamlFilePath, out instance) || !ReferenceEquals(instance.Workspace, workspace))
                return PreviewerLaunchResult.Fail("The previewer was closed or replaced.");
            // A later update overtook this one; the host renders the later document.
            if (latestTextOnly && instance.XamlText != xamlText) return new PreviewerLaunchResult(true, null, instance.Process);
            // A host keeps the resources and theme variant a provider applied, and a later theme does not reliably
            // reach the controls it creates on every platform; a different theme therefore starts a fresh host,
            // which applies the theme before it loads the document.
            if (instance.LastSentThemeRequestId is { } applied && applied != themeRequest?.RequestId)
            {
                replacement = new PreviewerInstance
                {
                    Workspace = instance.Workspace, WorkspacePath = instance.WorkspacePath, Log = instance.Log,
                    XamlText = xamlText, ThemeRequest = themeRequest
                };
                instance.ReplacedBy = replacement;
                _instances[xamlFilePath] = replacement;
            }
            else
            {
                instance.XamlText = xamlText;
                instance.ThemeRequest = themeRequest;
                // A host that is still starting loads the latest document itself.
                if (instance.Session is null) return new PreviewerLaunchResult(true, null, instance.Process);
            }
        }

        if (replacement is not null)
        {
            instance.Started.TrySetResult(PreviewerLaunchResult.Fail("The previewer restarted for another theme."));
            DisposeInstance(instance);
            _ = StartInstanceAsync(xamlFilePath, replacement, workspace, replacement.WorkspacePath, null, replacement.Log);
            (_, PreviewerLaunchResult restarted) = await WaitForStartAsync(replacement);
            if (restarted.Success) log?.Invoke("Info", "Previewer restarted for the selected theme");
            return restarted;
        }

        if (!TryCreateLaunchInfo(xamlFilePath, workspace, workspacePath, out PreviewerLaunchInfo launchInfo, out string error))
            return PreviewerLaunchResult.Fail(error);

        await SendInstanceUpdateAsync(xamlFilePath, instance, xamlText, launchInfo, fullRender);
        log?.Invoke("Info", "Previewer XAML update sent");
        return new PreviewerLaunchResult(true, null, instance.Process);
    }

    public bool TryGetSession(string xamlFilePath, out PreviewerTcpSession? session)
    {
        lock (_gate)
        {
            session = _instances.TryGetValue(xamlFilePath, out PreviewerInstance? instance) ? instance.Session : null;
            return session is not null;
        }
    }

    // A full render reloads the document even when nothing changed, so the host sends a new frame.
    private async Task SendInstanceUpdateAsync(string xamlFilePath, PreviewerInstance instance,
        string xamlText, PreviewerLaunchInfo launchInfo, bool fullRender = false)
    {
        await instance.UpdateGate.WaitAsync();
        try
        {
            if (!IsCurrent(xamlFilePath, instance) || instance.Session is not { } session) return;
            DesignerThemeRequest? themeRequest;
            lock (_gate)
            {
                if (instance.XamlText != xamlText) return;
                themeRequest = instance.ThemeRequest;
            }
            string projectPath = BuildXamlProjectPath(xamlFilePath, launchInfo.ProjectDirectory);
            string updatedXaml = NormalizeDesignDataContext(xamlText);
            (double? width, double? height) = TryGetDesignSize(updatedXaml);
            Guid? renderId = null;
            Guid? themeRequestId = themeRequest?.RequestId;
            try
            {
                XDocument preview = XDocument.Parse(updatedXaml);
                string? value = preview.Descendants().Attributes(XName.Get("DesignNodeIdentity.RenderId",
                    "clr-namespace:XamlVisualEditor.Designer.Rendering;assembly=XamlVisualEditor.Designer.Rendering"))
                    .FirstOrDefault()?.Value;
                if (Guid.TryParse(value, out Guid parsed)) renderId = parsed;
            }
            catch (System.Xml.XmlException) { }
            try
            {
                // Theme requests travel on standard input, never inside the document. The host confirms the request
                // before the document follows, and a changed theme reloads the document so that the load reports
                // the new catalog and a rejected selection.
                bool themeChanged = false;
                if (themeRequest is not null && themeRequest.RequestId != instance.LastSentThemeRequestId)
                {
                    await SendThemeRequestAsync(instance, themeRequest);
                    instance.LastSentThemeRequestId = themeRequest.RequestId;
                    themeChanged = true;
                }
                Guid? previousRenderId = instance.LastSentRenderId;
                await session.SendUpdateXamlAsync(updatedXaml, launchInfo.XamlAssemblyPath, projectPath, width, height, renderId, themeRequestId,
                    !themeChanged && !fullRender && previousRenderId is { } previous && renderId is { } current
                        ? () => TrySendIncrementalUpdateAsync(instance, previous, current, updatedXaml) : null);
                instance.LastSentRenderId = renderId;
            }
            catch (Exception) when (!IsCurrent(xamlFilePath, instance))
            {
                // Closing or replacing a host ends pending transport requests.
            }
        }
        finally { instance.UpdateGate.Release(); }
    }

    private static async Task<bool> TrySendIncrementalUpdateAsync(PreviewerInstance instance, Guid previous, Guid current, string xaml)
    {
        if (instance.Process is not { } process || process.HasExited) return false;
        Guid requestId = Guid.NewGuid();
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (instance) { instance.PendingUpdateId = requestId; instance.PendingUpdate = completion; }
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new DesignerUpdateRequest(requestId, previous, current, xaml)));
            await process.StandardInput.FlushAsync();
            return await completion.Task;
        }
        finally
        {
            lock (instance) { if (ReferenceEquals(instance.PendingUpdate, completion)) instance.PendingUpdate = null; }
        }
    }

    private static readonly TimeSpan ThemeConfirmationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Waits until the host has applied the request, so the document that follows renders with it.</summary>
    private static async Task SendThemeRequestAsync(PreviewerInstance instance, DesignerThemeRequest request)
    {
        if (instance.Process is not { } process || process.HasExited) return;
        TaskCompletionSource<DesignerThemeResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (instance) { instance.PendingThemeId = request.RequestId; instance.PendingTheme = completion; }
        try
        {
            await process.StandardInput.WriteLineAsync(DesignerThemeProtocol.RequestPrefix + JsonSerializer.Serialize(request));
            await process.StandardInput.FlushAsync();
            // A host that does not confirm must not hold back the document update; the load then reports the theme state.
            DesignerThemeResult result = await completion.Task.WaitAsync(ThemeConfirmationTimeout);
            instance.Log?.Invoke("Info", $"Previewer applied theme request {request.RequestId}"
                + (result.Error is { } error ? $": {error}" : string.Empty));
        }
        catch (TimeoutException)
        {
            instance.Log?.Invoke("Warning", $"Previewer did not confirm theme request {request.RequestId} within {ThemeConfirmationTimeout.TotalSeconds:0} s");
        }
        finally
        {
            lock (instance) { if (ReferenceEquals(instance.PendingTheme, completion)) instance.PendingTheme = null; }
        }
    }

    private static bool TryReceiveThemeResult(PreviewerInstance instance, string message)
    {
        if (!message.StartsWith(DesignerThemeProtocol.ResultPrefix, StringComparison.Ordinal)) return false;
        try
        {
            DesignerThemeResult? result = JsonSerializer.Deserialize<DesignerThemeResult>(message[DesignerThemeProtocol.ResultPrefix.Length..]);
            lock (instance)
                if (result is not null && result.RequestId == instance.PendingThemeId)
                    instance.PendingTheme?.TrySetResult(result);
        }
        catch (JsonException) { }
        return true;
    }

    private static bool TryReceiveIncrementalResult(PreviewerInstance instance, string message)
    {
        if (!message.StartsWith(DesignerUpdateProtocol.ResultPrefix, StringComparison.Ordinal)) return false;
        try
        {
            DesignerUpdateResult? result = JsonSerializer.Deserialize<DesignerUpdateResult>(message[DesignerUpdateProtocol.ResultPrefix.Length..]);
            lock (instance)
                if (result is not null && result.RequestId == instance.PendingUpdateId)
                    instance.PendingUpdate?.TrySetResult(result.Applied);
        }
        catch (JsonException) { }
        return true;
    }

    private bool IsCurrent(string xamlFilePath, PreviewerInstance instance)
    {
        lock (_gate)
            return !_disposed && _instances.TryGetValue(xamlFilePath, out PreviewerInstance? current)
                && ReferenceEquals(current, instance);
    }

    private void OnProcessExited(string xamlFilePath, PreviewerInstance instance, Action<string, string>? log)
    {
        lock (_gate)
        {
            if (!IsCurrent(xamlFilePath, instance)) return;
            int exitCode = instance.Process!.ExitCode;
            if (exitCode != 0)
            {
                _telemetry.RecordCrash(exitCode);
                if (string.IsNullOrWhiteSpace(instance.Session?.LastError))
                    instance.Session?.ReportError(string.Format(UiStrings.PreviewerExitedWithCode, exitCode));
            }
            log?.Invoke("Info", $"Previewer exited with code {exitCode}");
        }
        if (ReleaseInstance(xamlFilePath, instance, "The previewer process exited.")) PreviewerExited?.Invoke(xamlFilePath);
    }

    private bool ReleaseInstance(string xamlFilePath, PreviewerInstance instance, string reason)
    {
        lock (_gate)
        {
            if (!_instances.TryGetValue(xamlFilePath, out PreviewerInstance? current)
                || !ReferenceEquals(current, instance)) return false;
            _instances.Remove(xamlFilePath);
        }
        instance.Started.TrySetResult(PreviewerLaunchResult.Fail(reason));
        DisposeInstance(instance);
        return true;
    }

    /// <summary>Ends the previewer process of a file and disposes its session.</summary>
    public void StopPreviewer(string xamlFilePath, Process? expectedProcess = null)
    {
        PreviewerInstance? instance;
        lock (_gate)
        {
            if (!_instances.TryGetValue(xamlFilePath, out instance) ||
                (expectedProcess is not null && !ReferenceEquals(instance.Process, expectedProcess))) return;
            _instances.Remove(xamlFilePath);
        }
        instance.Started.TrySetResult(PreviewerLaunchResult.Fail("The previewer was closed."));
        DisposeInstance(instance);
    }

    public void Dispose()
    {
        PreviewerInstance[] instances;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            instances = _instances.Values.ToArray();
            _instances.Clear();
        }
        // Every host is ended before the first wait, so one slow host does not delay ending the others.
        List<PreviewerInstance> ending = new();
        foreach (PreviewerInstance instance in instances)
        {
            instance.Started.TrySetResult(PreviewerLaunchResult.Fail("The previewer service is closed."));
            if (EndInstance(instance) is not null) ending.Add(instance);
        }

        // Closing the service waits for the hosts, so no process outlives the editor.
        Stopwatch elapsed = Stopwatch.StartNew();
        foreach (PreviewerInstance instance in ending)
        {
            TimeSpan remaining = ProcessExitTimeout - elapsed.Elapsed;
            WaitForExitAndDispose(instance, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        }
    }

    // The wait runs off the calling thread; closing a document must not block the UI thread.
    private void DisposeInstance(PreviewerInstance instance)
    {
        if (EndInstance(instance) is not null)
            _ = Task.Run(() => WaitForExitAndDispose(instance, ProcessExitTimeout));
    }

    private static Process? EndInstance(PreviewerInstance instance)
    {
        lock (instance)
        {
            instance.PendingUpdate?.TrySetCanceled();
            instance.PendingTheme?.TrySetCanceled();
        }
        instance.Session?.Dispose();
        if (instance.Process is not { } process) return null;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception ex)
        {
            instance.Log?.Invoke("Warning", $"The previewer process could not be ended: {ex.Message}");
        }
        return process;
    }

    private void WaitForExitAndDispose(PreviewerInstance instance, TimeSpan timeout)
    {
        if (instance.Process is not { } process) return;
        try
        {
            if (!WaitForProcessExit(process, timeout))
                instance.Log?.Invoke("Warning", "The previewer process did not exit after it was ended.");
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    private static ProcessStartInfo BuildStartInfo(PreviewerLaunchInfo launchInfo)
    {
        (string runtimeConfigPath, string depsFilePath) = ResolveHostRuntimeFiles(launchInfo);
        string args = string.Join(" ", new[]
        {
            "exec",
            $"--runtimeconfig \"{runtimeConfigPath}\"",
            $"--depsfile \"{depsFilePath}\"",
            $"\"{launchInfo.HostPath}\"",
            $"--transport {launchInfo.TransportUri}",
            launchInfo.MethodArguments,
            $"\"{launchInfo.TargetAssemblyPath}\""
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        return new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = launchInfo.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static (string RuntimeConfigPath, string DepsFilePath) ResolveHostRuntimeFiles(PreviewerLaunchInfo launchInfo)
    {
        string hostRuntimeConfig = Path.ChangeExtension(launchInfo.HostPath, ".runtimeconfig.json");
        string hostDepsFile = Path.ChangeExtension(launchInfo.HostPath, ".deps.json");

        if (File.Exists(hostRuntimeConfig) && File.Exists(hostDepsFile))
        {
            return (hostRuntimeConfig, hostDepsFile);
        }

        return (launchInfo.RuntimeConfigPath, launchInfo.DepsFilePath);
    }

    private static bool TryCreateLaunchInfo(
        string xamlFilePath,
        WorkspaceModel workspace,
        string? workspacePath,
        out PreviewerLaunchInfo launchInfo,
        out string error)
    {
        launchInfo = default!;
        error = string.Empty;

        ProjectModel? project = ProjectSelection.FindPreviewProject(workspace, workspacePath, xamlFilePath);
        if (project is null)
        {
            error = UiStrings.NoProjectForXaml;
            return false;
        }

        string? projectDir = Path.GetDirectoryName(project.ProjectPath);

        string? xamlAssemblyPath = ProjectSelection.ResolveTargetAssemblyPath(project);
        if (string.IsNullOrWhiteSpace(xamlAssemblyPath) || !File.Exists(xamlAssemblyPath))
        {
            error = UiStrings.ProjectAssemblyMissing;
            return false;
        }

        // Prefer the configured application and its resources. The host can also
        // bootstrap a standalone library without an executable entry point.
        string targetAssemblyPath = xamlAssemblyPath;
        if (!project.IsExecutable)
        {
            string? hostApplication = ResolveHostApplicationPath(
                xamlAssemblyPath,
                workspace.Projects,
                Environment.GetEnvironmentVariable("XVE_PREVIEWER_APP_ASSEMBLY"));
            targetAssemblyPath = hostApplication ?? xamlAssemblyPath;
        }

        string? outputDir = Path.GetDirectoryName(targetAssemblyPath);
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            error = UiStrings.ProjectOutputDirectoryUnknown;
            return false;
        }

        string baseName = Path.GetFileNameWithoutExtension(targetAssemblyPath);
        string runtimeConfigPath = Path.Combine(outputDir, baseName + ".runtimeconfig.json");
        string depsFilePath = Path.Combine(outputDir, baseName + ".deps.json");

        string hostPath = Path.Combine(AppContext.BaseDirectory, "XamlVisualEditor.Designer.PreviewerHost.dll");
        string transportUri = string.Empty;

        launchInfo = new PreviewerLaunchInfo(
            hostPath,
            targetAssemblyPath,
            xamlAssemblyPath,
            runtimeConfigPath,
            depsFilePath,
            outputDir,
            transportUri,
            string.Empty,
            projectDir);

        return true;
    }

    internal static string? ResolveHostApplicationPath(
        string xamlAssemblyPath,
        IEnumerable<ProjectModel> projects,
        string? appOverride)
    {
        if (IsHostApplication(appOverride))
        {
            return Path.GetFullPath(appOverride!);
        }

        ProjectModel? executable = ProjectSelection.SelectPreferredProject(projects.Where(p => p.IsExecutable));
        string? executablePath = executable is null
            ? null
            : ProjectSelection.ResolveTargetAssemblyPath(executable);
        if (IsHostApplication(executablePath))
        {
            return Path.GetFullPath(executablePath!);
        }

        string? outputDirectory = Path.GetDirectoryName(xamlAssemblyPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            return null;
        }

        DirectoryInfo? ancestor = new(outputDirectory);
        for (int depth = 0; ancestor is not null && depth < 4; depth++, ancestor = ancestor.Parent)
        {
            string? candidate = FindHostApplication(outputDirectory, ancestor.Name);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        try
        {
            string[] candidates = Directory.EnumerateFiles(
                    outputDirectory,
                    "*.runtimeconfig.json",
                    SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileName(path)[..^".runtimeconfig.json".Length])
                .Select(name => FindHostApplication(outputDirectory, name))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(FileSystemPathComparison.Comparer)
                .ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindHostApplication(string outputDirectory, string baseName)
    {
        string dllPath = Path.Combine(outputDirectory, baseName + ".dll");
        if (IsHostApplication(dllPath))
        {
            return dllPath;
        }

        string exePath = Path.Combine(outputDirectory, baseName + ".exe");
        return IsHostApplication(exePath) ? exePath : null;
    }

    private static bool IsHostApplication(string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
        {
            return false;
        }

        string runtimeConfigPath = Path.ChangeExtension(assemblyPath, ".runtimeconfig.json");
        string depsFilePath = Path.ChangeExtension(assemblyPath, ".deps.json");
        return File.Exists(runtimeConfigPath) && File.Exists(depsFilePath);
    }

    private static void LogCompiledBindingsSetting(string assemblyPath, Action<string, string>? log)
    {
        if (!TryGetAssemblyMetadataValue(assemblyPath, "AvaloniaUseCompiledBindingsByDefault", out string? value))
        {
            return;
        }

        if (bool.TryParse(value, out bool enabled))
        {
            log?.Invoke("Info", $"Compiled bindings default: {enabled}");
        }
    }

    internal static bool TryGetAssemblyMetadataValue(
        string assemblyPath,
        string key,
        out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
        {
            return false;
        }

        try
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using PEReader pe = new(stream);
            if (!pe.HasMetadata) return false;
            MetadataReader metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) return false;
            foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                CustomAttribute attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                MemberReference constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                if (metadata.GetString(type.Namespace) != "System.Reflection"
                    || metadata.GetString(type.Name) != nameof(AssemblyMetadataAttribute)) continue;
                BlobReader blob = metadata.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() != 1 || !string.Equals(blob.ReadSerializedString(), key, StringComparison.Ordinal)) continue;
                value = blob.ReadSerializedString();
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string BuildXamlProjectPath(string xamlFilePath, string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            return xamlFilePath;
        }

        try
        {
            string relative = Path.GetRelativePath(projectDir, xamlFilePath);
            relative = relative.Replace(Path.DirectorySeparatorChar, '/');
            if (!relative.StartsWith('/'))
            {
                relative = "/" + relative;
            }

            return relative;
        }
        catch
        {
            return xamlFilePath;
        }
    }

    private static string NormalizeDesignDataContext(string xamlText)
    {
        if (string.IsNullOrWhiteSpace(xamlText))
        {
            return xamlText;
        }

        try
        {
            XDocument doc = XDocument.Parse(xamlText, LoadOptions.PreserveWhitespace);
            if (doc.Root is null)
            {
                return xamlText;
            }

            // The previewer's runtime compiler has to instantiate the x:Class root itself and
            // needs a parameterless constructor for it; many forms only have parameterized
            // ones. The design copy therefore loads without x:Class (the content tree under the
            // plain root element), which does not matter for the layout preview.
            doc.Root.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Remove();

            foreach (XElement element in doc.Descendants())
            {
                if (element.Attribute("Design.DataContext") is not null)
                {
                    continue;
                }

                foreach (XAttribute attr in element.Attributes())
                {
                    if (!IsDesignNamespace(attr.Name.NamespaceName))
                    {
                        continue;
                    }

                    if (!string.Equals(attr.Name.LocalName, "DataContext", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (LooksLikeMarkupExtension(attr.Value))
                    {
                        continue;
                    }

                    element.SetAttributeValue("Design.DataContext", attr.Value);
                    break;
                }
            }

            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            return xamlText;
        }
    }

    private static bool IsDesignNamespace(string? xmlNamespace)
    {
        if (string.IsNullOrWhiteSpace(xmlNamespace))
        {
            return false;
        }

        return string.Equals(xmlNamespace, "http://schemas.microsoft.com/expression/blend/2008", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeMarkupExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        return trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal);
    }

    private static (double? Width, double? Height) TryGetDesignSize(string xamlText)
    {
        if (string.IsNullOrWhiteSpace(xamlText))
        {
            return (null, null);
        }

        double? width = TryMatchDesignDimension(xamlText, "DesignWidth")
            ?? TryMatchDesignDimension(xamlText, "Width");
        double? height = TryMatchDesignDimension(xamlText, "DesignHeight")
            ?? TryMatchDesignDimension(xamlText, "Height");

        return (width, height);
    }

    private static double? TryMatchDesignDimension(string text, string propertyName)
    {
        System.Text.RegularExpressions.Regex regex = new(
            $"\\b(?:\\w+:)?{propertyName}\\s*=\\s*\"(?<value>[0-9]+(?:\\.[0-9]+)?)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        System.Text.RegularExpressions.Match match = regex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        string value = match.Groups["value"].Value;
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double result)
            ? result
            : null;
    }
}

internal readonly record struct PreviewerLaunchInfo(
    string HostPath,
    string TargetAssemblyPath,
    string XamlAssemblyPath,
    string RuntimeConfigPath,
    string DepsFilePath,
    string WorkingDirectory,
    string TransportUri,
    string MethodArguments,
    string? ProjectDirectory)
{
    // Class libraries produce no runtimeconfig.json of their own; the process start then uses
    // the runtime files of the PreviewerHost (see ResolveHostRuntimeFiles).
    public bool HasRequiredAssets =>
        File.Exists(HostPath) &&
        File.Exists(TargetAssemblyPath) &&
        ((File.Exists(RuntimeConfigPath) && File.Exists(DepsFilePath)) || HasHostRuntimeFiles);

    private bool HasHostRuntimeFiles =>
        File.Exists(Path.ChangeExtension(HostPath, ".runtimeconfig.json")) &&
        File.Exists(Path.ChangeExtension(HostPath, ".deps.json"));
}

internal readonly record struct PreviewerLaunchResult(bool Success, string? ErrorMessage, Process? Process)
{
    public static PreviewerLaunchResult FromProcess(Process process) => new(true, null, process);

    public static PreviewerLaunchResult Fail(string message) => new(false, message, null);
}

public sealed record PreviewerErrorInfo(string Message, int? Line, int? Column, string? FilePath,
    IReadOnlyList<PreviewerElementPathSegment>? ElementPath = null, string? TextContent = null, string? AttributeName = null);

/// <summary>Maps an element of the preview to its element in the source document.</summary>
public sealed record PreviewerElementPathSegment(string Namespace, string Name, int Index);

internal sealed class PreviewerTelemetryStore
{
    private readonly object _gate = new();
    private PreviewerTelemetryData _data;

    public PreviewerTelemetryStore()
    {
        _data = Load();
    }

    public void RecordStart(TimeSpan duration)
    {
        lock (_gate)
        {
            _data.StartCount++;
            _data.LastStartUtc = DateTimeOffset.UtcNow;
            _data.LastStartupMs = Math.Round(duration.TotalMilliseconds, 2);
            Save(_data);
        }
    }

    public void RecordCrash(int? exitCode)
    {
        lock (_gate)
        {
            _data.CrashCount++;
            _data.LastCrashUtc = DateTimeOffset.UtcNow;
            _data.LastExitCode = exitCode;
            Save(_data);
        }
    }

    public void RecordFailure(string? reason)
    {
        lock (_gate)
        {
            _data.FailureCount++;
            _data.LastFailureUtc = DateTimeOffset.UtcNow;
            _data.LastFailureReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
            Save(_data);
        }
    }

    private static PreviewerTelemetryData Load()
    {
        try
        {
            string path = GetTelemetryPath();
            if (!File.Exists(path))
            {
                return new PreviewerTelemetryData();
            }

            string json = SharedFileReader.ReadAllText(path);
            PreviewerTelemetryData? data = JsonSerializer.Deserialize<PreviewerTelemetryData>(json);
            return data ?? new PreviewerTelemetryData();
        }
        catch
        {
            return new PreviewerTelemetryData();
        }
    }

    private static void Save(PreviewerTelemetryData data)
    {
        try
        {
            string path = GetTelemetryPath();
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
        }
    }

    private static string GetTelemetryPath()
    {
        return Path.Combine(ShellSettingsPaths.GetRoot(), "previewer-telemetry.json");
    }

    private sealed class PreviewerTelemetryData
    {
        public int StartCount { get; set; }
        public int CrashCount { get; set; }
        public int FailureCount { get; set; }
        public double LastStartupMs { get; set; }
        public DateTimeOffset? LastStartUtc { get; set; }
        public DateTimeOffset? LastCrashUtc { get; set; }
        public DateTimeOffset? LastFailureUtc { get; set; }
        public int? LastExitCode { get; set; }
        public string? LastFailureReason { get; set; }
    }
}
