using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// Gets how long the output is still read after git ended. A process that git started, such as a hook that left
    /// a background process behind, can keep the output pipes open; the result then holds the output read so far.
    /// </summary>
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly GitProcessOptions _options;
    private readonly ILogger _logger;
    private readonly Lazy<GitExecutable> _executable;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeGates = new(FileSystemPathComparison.Comparer);

    public GitProcessRunner(GitProcessOptions options, GitExecutableLocator? locator = null, ILogger? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _executable = new Lazy<GitExecutable>(
            () =>
            {
                GitExecutableLocator resolver = locator ?? new GitExecutableLocator();
                return options.ExecutablePath is { } path
                    ? resolver.ResolveLauncher(path)
                    : resolver.Locate() ?? new GitExecutable("git");
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Gets the git executable the processes start with.</summary>
    public string ExecutablePath => _executable.Value.Path;

    /// <summary>
    /// Runs git and returns its exit code and output. Cancelling <paramref name="ct"/> while git runs kills git and
    /// every process it started, waits for git to end and throws <see cref="OperationCanceledException"/>; an elapsed
    /// <see cref="GitProcessRequest.Timeout"/> does the same and throws <see cref="TimeoutException"/>. A process
    /// that ended before the kill returns its result.
    /// </summary>
    /// <param name="request">The git process to run.</param>
    /// <param name="ct">Cancels the wait for the other writing commands and the process.</param>
    /// <param name="waitCancellation">
    /// Cancels only the wait for the other writing commands instead of <paramref name="ct"/>, for a command that must
    /// not be stopped once it started.
    /// </param>
    public async Task<GitProcessResult> RunAsync(
        GitProcessRequest request,
        CancellationToken ct,
        CancellationToken? waitCancellation = null)
    {
        SemaphoreSlim? writeGate = request.Access == GitCommandAccess.Write ? GetWriteGate(request.WorkingDirectory) : null;
        if (writeGate is not null)
        {
            Task wait = writeGate.WaitAsync(waitCancellation ?? ct);
            if (!wait.IsCompleted)
            {
                _options.WriteGateWaitStarted?.Invoke(request.Arguments);
            }

            await wait.ConfigureAwait(false);
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

        // Assigned right after the start. Process.Start cannot create the process suspended, so a process that git
        // starts between its creation and the assignment stays outside the job and is reached only through the
        // process tree. For a resolved Git for Windows layout the process is git itself, not the launcher, which
        // would start git at once (see GitExecutableLocator.ResolveLauncher); git starts a hook, a remote helper, ssh
        // or a credential helper only after it read its configuration and the repository. Another wrapper, such as
        // a script on PATH, can start git before the assignment. Creating the process suspended would need
        // CreateProcess with own pipes instead of Process.
        using GitProcessJob? job = OperatingSystem.IsWindows() ? GitProcessJob.TryAssign(process, _logger) : null;

        // Set only when cancellation or the timeout killed the running process; a process that ended on its own
        // keeps its result even when the token is cancelled afterwards.
        int killed = 0;
        CancellationTokenRegistration killRegistration = token.Register(() =>
        {
            if (Kill(process, job))
            {
                Volatile.Write(ref killed, 1);
            }
        });
        // Not linked to the token: closing the input on cancellation could let git end on its own before the kill,
        // and the cancelled command would then report a result. The input stops when the process has ended.
        using CancellationTokenSource inputCancellation = new();

        OutputBuffer output = new();
        OutputBuffer error = new();
        // Output beyond the line or byte limit is not read; git, which would block on the full pipe, is stopped instead.
        Task outputTask = output.ReadAllAsync(
            process.StandardOutput.BaseStream,
            request.MaxOutputLines,
            request.MaxOutputBytes,
            () => Kill(process, job));
        Task errorTask = error.ReadAllAsync(process.StandardError.BaseStream);
        Task inputTask = WriteInputAsync(process.StandardInput, request.StandardInput, inputCancellation.Token);

        try
        {
            request.ProcessStarted?.Invoke(process.Id);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            // Bounded also when git ended on its own; cancelling during this wait still kills what git left behind.
            Task outputDrained = Task.WhenAll(outputTask, errorTask);
            if (await Task.WhenAny(outputDrained, Task.Delay(OutputDrainTimeout, CancellationToken.None)).ConfigureAwait(false) != outputDrained)
            {
                _logger.LogWarning(
                    "The output of git {Command} was still open {Timeout} after git ended; a process that git started still holds it. The result holds the output read so far.",
                    FirstArgument(request),
                    OutputDrainTimeout);

                // The readers end with an error once the pipes close or the process is disposed.
                _ = outputDrained.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch
        {
            // A failing ProcessStarted callback must not leave git running.
            Kill(process, job);
            throw;
        }
        finally
        {
            killRegistration.Dispose();
            job?.Release();

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

        if (Volatile.Read(ref killed) != 0)
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"git {FirstArgument(request)} did not finish within {request.Timeout}.");
        }

        return new GitProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = Utf8NoBom.GetString(output.ToArray()),
            StandardError = Utf8NoBom.GetString(error.ToArray()),
            IsOutputTruncated = output.IsTruncated
        };
    }

    private ProcessStartInfo CreateStartInfo(GitProcessRequest request)
    {
        GitExecutable executable = _executable.Value;
        ProcessStartInfo startInfo = new()
        {
            FileName = executable.Path,
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
        if (request.Access == GitCommandAccess.Read)
        {
            // Read commands such as status must not refresh the index, which would wake up index watchers.
            environment["GIT_OPTIONAL_LOCKS"] = "0";
        }

        ApplyEnvironment(environment, _options.Environment);
        if (request.Environment is not null)
        {
            ApplyEnvironment(environment, request.Environment);
        }

        if (executable.Layout is { } layout)
        {
            ApplyLauncherEnvironment(environment, layout);
        }

        return startInfo;
    }

    /// <summary>
    /// Changes <paramref name="environment"/> as the Git for Windows launcher changes the environment it was started
    /// with: it sets <c>MSYSTEM</c>, also over another value, sets <c>HOME</c> when it is missing, and puts the
    /// folders of the executable and then <c>%HOME%\bin</c> in front of PATH. Git started without <c>MSYSTEM</c> would
    /// set it itself and put <c>%HOME%\bin</c> in front of the folders of Git for Windows, so a program there, such as
    /// an own ssh, would replace the one of Git for Windows.
    /// </summary>
    private static void ApplyLauncherEnvironment(IDictionary<string, string?> environment, GitForWindowsLayout layout)
    {
        environment["MSYSTEM"] = layout.MsysEnvironment;
        List<string> folders = new(layout.SearchPathFolders);
        if (GetLauncherHome(environment) is { } home)
        {
            environment["HOME"] = home;
            folders.Add(Path.Combine(home, "bin"));
        }

        // The variable names of the environment ignore case on Windows, so this also finds "Path".
        string? searchPath = environment.TryGetValue("PATH", out string? current) ? current : null;
        environment["PATH"] = string.Join(Path.PathSeparator, folders)
            + (string.IsNullOrEmpty(searchPath) ? string.Empty : Path.PathSeparator + searchPath);
    }

    /// <summary>
    /// Returns <c>HOME</c> as the launcher determines it: the variable when it is set, otherwise <c>HOMEDRIVE</c>
    /// followed by <c>HOMEPATH</c> when that folder exists, otherwise <c>USERPROFILE</c>; null when none of them is
    /// set.
    /// </summary>
    private static string? GetLauncherHome(IDictionary<string, string?> environment)
    {
        if (Read("HOME") is { } home)
        {
            return home;
        }

        if (Read("HOMEPATH") is { } homePath)
        {
            string homeShare = (Read("HOMEDRIVE") ?? string.Empty) + homePath;
            if (Directory.Exists(homeShare))
            {
                return homeShare;
            }
        }

        return Read("USERPROFILE");

        string? Read(string name) =>
            environment.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value) ? value : null;
    }

    private static void ApplyEnvironment(IDictionary<string, string?> environment, IReadOnlyDictionary<string, string?> variables)
    {
        foreach (KeyValuePair<string, string?> variable in variables)
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

    /// <summary>
    /// Kills git and the processes it started: on Windows every process of its job, which also reaches processes
    /// whose parent already ended, otherwise the process tree. Returns false when git had already ended; the
    /// processes git left behind are killed then too.
    /// </summary>
    private bool Kill(Process process, GitProcessJob? job)
    {
        bool running;
        try
        {
            running = !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            running = false;
        }

        if (job is not null && job.Terminate())
        {
            return running;
        }

        if (!running)
        {
            return false;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The process has already ended.
            return false;
        }
        catch (Win32Exception exception)
        {
            // The process is ending and can no longer be terminated.
            _logger.LogDebug(exception, "git process {ProcessId} could not be killed.", process.Id);
            return false;
        }
        catch (AggregateException exception)
        {
            // Kill(entireProcessTree) tries every process of the tree and then reports those it could not terminate,
            // for example one that was ending at that moment and denied access. The kill was carried out.
            foreach (Exception inner in exception.InnerExceptions)
            {
                _logger.LogWarning(inner, "A child process of git process {ProcessId} could not be killed.", process.Id);
            }

            return true;
        }
    }

    /// <summary>Collects the bytes of an output stream; the bytes read so far can be taken while reading goes on.</summary>
    private sealed class OutputBuffer
    {
        private readonly MemoryStream _buffer = new();
        private volatile bool _isTruncated;

        /// <summary>Gets whether the stream had more lines or bytes than the limits and reading stopped there.</summary>
        public bool IsTruncated => _isTruncated;

        /// <summary>
        /// Reads the stream to its end, or up to the end of line <paramref name="maxLines"/> or of the last complete
        /// line within <paramref name="maxBytes"/> when more bytes follow; then <paramref name="truncated"/> runs and
        /// the rest is not read. Cut output keeps only complete lines.
        /// </summary>
        public async Task ReadAllAsync(Stream stream, int? maxLines = null, int? maxBytes = null, Action? truncated = null)
        {
            byte[] chunk = new byte[16 * 1024];
            int lines = 0;
            long written = 0;
            int read;
            while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
            {
                int keep = maxBytes is { } byteLimit ? (int)Math.Min(read, byteLimit - written) : read;
                if (maxLines is { } lineLimit)
                {
                    keep = EndOfLines(chunk, keep, lineLimit, ref lines);
                }

                bool isTruncated = keep < read;
                lock (_buffer)
                {
                    _buffer.Write(chunk, 0, keep);
                    if (isTruncated)
                    {
                        // A byte limit can end inside a line, which may have started in an earlier chunk.
                        byte[] data = _buffer.GetBuffer();
                        int length = (int)_buffer.Length;
                        _buffer.SetLength(length == 0 ? 0 : Array.LastIndexOf(data, (byte)'\n', length - 1, length) + 1);
                    }
                }

                written += keep;
                if (isTruncated)
                {
                    _isTruncated = true;
                    truncated?.Invoke();
                    return;
                }
            }
        }

        public byte[] ToArray()
        {
            lock (_buffer)
            {
                return _buffer.ToArray();
            }
        }

        /// <summary>
        /// Returns the number of the first <paramref name="count"/> bytes of <paramref name="chunk"/> that belong to
        /// the first <paramref name="maxLines"/> lines, counting the lines of earlier chunks in <paramref name="lines"/>.
        /// </summary>
        private static int EndOfLines(byte[] chunk, int count, int maxLines, ref int lines)
        {
            int end = 0;
            while (lines < maxLines)
            {
                int newline = Array.IndexOf(chunk, (byte)'\n', end, count - end);
                if (newline < 0)
                {
                    return count;
                }

                lines++;
                end = newline + 1;
            }

            return end;
        }
    }

    private static string FirstArgument(GitProcessRequest request) =>
        request.Arguments.Count == 0 ? string.Empty : request.Arguments[0];
}

/// <summary>
/// How a git command accesses the repository, which decides whether it waits for the other commands of its working
/// directory.
/// </summary>
internal enum GitCommandAccess
{
    /// <summary>Only reads; runs in parallel to all other commands and with <c>GIT_OPTIONAL_LOCKS=0</c>.</summary>
    Read,

    /// <summary>Changes the index, the working tree or the branches; runs after the other writing commands.</summary>
    Write,

    /// <summary>
    /// Talks to a remote and updates only references, which git locks itself; runs in parallel to all other
    /// commands, so a slow server or a sign-in does not hold up stage or commit.
    /// </summary>
    Network
}

/// <summary>
/// Describes one git process run by <see cref="GitProcessRunner"/>.
/// </summary>
internal sealed class GitProcessRequest
{
    public required string WorkingDirectory { get; init; }

    /// <summary>Gets the git arguments; <c>-c core.quotepath=off</c> is put in front of them.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Gets how the command accesses the repository; <see cref="GitCommandAccess.Read"/> by default.</summary>
    public GitCommandAccess Access { get; init; }

    /// <summary>
    /// Gets the bytes written to the standard input of git, which is closed when the stream ends; null closes the
    /// standard input at once. The stream must honor cancellation of its reads.
    /// </summary>
    public Stream? StandardInput { get; init; }

    /// <summary>Gets the time after which the process is killed, for commands that use the network.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Gets environment variables of this process, applied after those of <see cref="GitProcessOptions"/>; a null
    /// value removes the variable.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Gets a callback that receives the process id right after the process started.</summary>
    public Action<int>? ProcessStarted { get; init; }

    /// <summary>
    /// Gets the number of output lines that are read; when git writes more, the rest is not read, git is stopped and
    /// the result has <see cref="GitProcessResult.IsOutputTruncated"/>. Null reads all output.
    /// </summary>
    public int? MaxOutputLines { get; init; }

    /// <summary>
    /// Gets the number of output bytes that are read; when git writes more, the output ends with the last complete
    /// line within the limit, git is stopped and the result has <see cref="GitProcessResult.IsOutputTruncated"/>.
    /// The output keeps only complete lines, so it is empty when the first line is longer than the limit. Null reads
    /// all output.
    /// </summary>
    public int? MaxOutputBytes { get; init; }

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

    public string StandardError { get; init; } = string.Empty;

    /// <summary>
    /// Gets whether the output had more lines than <see cref="GitProcessRequest.MaxOutputLines"/> or more bytes than
    /// <see cref="GitProcessRequest.MaxOutputBytes"/>; git was stopped then, so <see cref="ExitCode"/> does not tell
    /// whether it would have succeeded.
    /// </summary>
    public bool IsOutputTruncated { get; init; }
}
