using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Workspace;

/// <summary>
/// Runs git processes: arguments are passed unquoted, output and error output are decoded as UTF-8, standard input
/// is written from the request or closed at once, and git never prompts on the terminal or opens an editor.
/// Writing commands of the same working directory run one after another.
/// </summary>
internal sealed class GitProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly GitProcessOptions _options;
    private readonly Lazy<string> _executablePath;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeGates = new(FileSystemPathComparison.Comparer);

    public GitProcessRunner(GitProcessOptions options, GitExecutableLocator? locator = null)
    {
        _options = options;
        _executablePath = new Lazy<string>(
            () => options.ExecutablePath ?? (locator ?? new GitExecutableLocator()).Locate() ?? "git",
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Gets the git executable the processes start with.</summary>
    public string ExecutablePath => _executablePath.Value;

    /// <summary>
    /// Runs git and returns its exit code and output. Cancelling <paramref name="ct"/> kills the process tree, waits
    /// for the process to end and throws <see cref="OperationCanceledException"/>; an elapsed
    /// <see cref="GitProcessRequest.Timeout"/> does the same and throws <see cref="TimeoutException"/>.
    /// </summary>
    public async Task<GitProcessResult> RunAsync(GitProcessRequest request, CancellationToken ct)
    {
        SemaphoreSlim? writeGate = request.IsReadOnly ? null : GetWriteGate(request.WorkingDirectory);
        if (writeGate is not null)
        {
            await writeGate.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            return await RunProcessAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            writeGate?.Release();
        }
    }

    private SemaphoreSlim GetWriteGate(string workingDirectory)
    {
        string key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        return _writeGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
    }

    private async Task<GitProcessResult> RunProcessAsync(GitProcessRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using CancellationTokenSource? timeoutSource = request.Timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSource is not null && request.Timeout is { } timeout)
        {
            timeoutSource.CancelAfter(timeout);
        }

        CancellationToken token = timeoutSource?.Token ?? ct;

        using Process process = new() { StartInfo = CreateStartInfo(request) };
        process.Start();
        request.ProcessStarted?.Invoke(process.Id);

        using CancellationTokenRegistration killRegistration = token.Register(
            static state => KillProcessTree((Process)state!),
            process);
        using CancellationTokenSource inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task<byte[]> outputTask = ReadAllBytesAsync(process.StandardOutput.BaseStream);
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        Task inputTask = WriteInputAsync(process.StandardInput, request.StandardInput, inputCancellation.Token);

        try
        {
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // Git may end without reading all of its input; the copy must not outlive the process.
            inputCancellation.Cancel();
            try
            {
                await inputTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (token.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"git {FirstArgument(request)} did not finish within {request.Timeout}.");
        }

        byte[] output = outputTask.Result;
        return new GitProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = request.CaptureRawOutput ? string.Empty : Utf8NoBom.GetString(output),
            RawStandardOutput = request.CaptureRawOutput ? output : null,
            StandardError = errorTask.Result
        };
    }

    private ProcessStartInfo CreateStartInfo(GitProcessRequest request)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _executablePath.Value,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Paths in the output are written as they are instead of as quoted octal escapes.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=off");
        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        IDictionary<string, string?> environment = startInfo.Environment;
        environment["GIT_TERMINAL_PROMPT"] = "0";
        // Paths are literal file names; glob characters such as '[' or '*' must not widen a command to other files.
        environment["GIT_LITERAL_PATHSPECS"] = "1";
        environment["GIT_EDITOR"] = "true";
        environment["GIT_SEQUENCE_EDITOR"] = "true";
        if (request.IsReadOnly)
        {
            // Read commands such as status must not refresh the index, which would wake up index watchers.
            environment["GIT_OPTIONAL_LOCKS"] = "0";
        }

        foreach (KeyValuePair<string, string?> variable in _options.Environment)
        {
            if (variable.Value is null)
            {
                environment.Remove(variable.Key);
            }
            else
            {
                environment[variable.Key] = variable.Value;
            }
        }

        return startInfo;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task WriteInputAsync(StreamWriter standardInput, Stream? input, CancellationToken ct)
    {
        try
        {
            if (input is not null)
            {
                await input.CopyToAsync(standardInput.BaseStream, ct).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // Git closed its input before it read everything, for example because it failed early.
        }
        finally
        {
            try
            {
                standardInput.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process has already ended.
        }
        catch (Win32Exception)
        {
            // The process is ending and can no longer be terminated.
        }
    }

    private static string FirstArgument(GitProcessRequest request) =>
        request.Arguments.Count == 0 ? string.Empty : request.Arguments[0];
}

/// <summary>
/// Describes one git process run by <see cref="GitProcessRunner"/>.
/// </summary>
internal sealed class GitProcessRequest
{
    public required string WorkingDirectory { get; init; }

    /// <summary>Gets the git arguments; <c>-c core.quotepath=off</c> is put in front of them.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>
    /// Gets whether the command only reads the repository. Read commands run with <c>GIT_OPTIONAL_LOCKS=0</c> and
    /// in parallel; all other commands of a working directory run one after another.
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// Gets the bytes written to the standard input of git, which is closed when the stream ends; null closes the
    /// standard input at once. The stream must honor cancellation of its reads.
    /// </summary>
    public Stream? StandardInput { get; init; }

    /// <summary>Gets whether the output is returned as bytes in <see cref="GitProcessResult.RawStandardOutput"/> instead of as text.</summary>
    public bool CaptureRawOutput { get; init; }

    /// <summary>Gets the time after which the process is killed, for commands that use the network.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Gets a callback that receives the process id right after the process started.</summary>
    public Action<int>? ProcessStarted { get; init; }

    /// <summary>Creates a standard input stream with the UTF-8 bytes of <paramref name="text"/>, without byte order mark.</summary>
    public static Stream Utf8Input(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
}

/// <summary>
/// Exit code and output of a git process.
/// </summary>
internal sealed class GitProcessResult
{
    public int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public byte[]? RawStandardOutput { get; init; }

    public string StandardError { get; init; } = string.Empty;
}
