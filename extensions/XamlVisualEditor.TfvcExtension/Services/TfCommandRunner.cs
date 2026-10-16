using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>Runs tf.exe commands.</summary>
public interface ITfCommandRunner
{
    /// <summary>Gets or sets an explicit tf.exe path; null selects automatic detection.</summary>
    string? ConfiguredTfExePath { get; set; }

    /// <summary>Runs tf.exe with the given arguments.</summary>
    /// <param name="workingDirectory">Directory whose workspace mapping tf.exe uses when no workspace is passed explicitly.</param>
    /// <param name="arguments">Command and arguments; each entry is passed as one argument.</param>
    /// <param name="timeout">Maximum run time before the process is terminated.</param>
    /// <param name="cancellationToken">Cancels the command and terminates the process.</param>
    Task<TfCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Raised when tf.exe cannot be located or started.</summary>
public sealed class TfExeNotFoundException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TfExeNotFoundException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Runs tf.exe as a child process in the context of the current Windows user.</summary>
public sealed class TfCommandRunner : ITfCommandRunner
{
    /// <summary>Longest command line that is passed to tf.exe; Windows limits command lines to 32767 characters.</summary>
    public const int MAX_COMMAND_LINE_LENGTH = 30000;

    private readonly ITfExeLocator _locator;
    private readonly IExtensionLogger? _logger;
    private readonly Encoding _outputEncoding;

    /// <summary>Creates the runner.</summary>
    /// <param name="locator">Locates tf.exe.</param>
    /// <param name="logger">Receives one entry per finished command with exit code, duration and working directory.</param>
    public TfCommandRunner(ITfExeLocator locator, IExtensionLogger? logger = null)
    {
        _locator = locator;
        _logger = logger;
        _outputEncoding = CreateOutputEncoding();
    }

    /// <inheritdoc />
    public string? ConfiguredTfExePath { get; set; }

    /// <inheritdoc />
    public async Task<TfCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string? tfExePath = _locator.Locate(ConfiguredTfExePath);
        if (tfExePath is null)
        {
            throw new TfExeNotFoundException("tf.exe was not found.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = tfExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = _outputEncoding,
            StandardErrorEncoding = _outputEncoding,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(workingDirectory)
                ? workingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new TfExeNotFoundException("tf.exe did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new TfExeNotFoundException("tf.exe could not be started: " + ex.Message, ex);
        }

        using (process)
        using (CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutSource.CancelAfter(timeout);
            process.StandardInput.Close();
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
                Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                TfCommandResult result = new(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
                LogResult(arguments, startInfo.WorkingDirectory, result, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException(string.Format(
                    CultureInfo.CurrentCulture,
                    TfvcStrings.TfCommandTimeoutFormat,
                    arguments.Count > 0 ? arguments[0] : string.Empty,
                    (int)timeout.TotalSeconds));
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
        }
    }

    /// <summary>Estimates the command line length of a tf.exe call.</summary>
    public static int EstimateCommandLineLength(IReadOnlyList<string> arguments)
    {
        int length = 260;
        foreach (string argument in arguments)
        {
            length += argument.Length + 3;
        }

        return length;
    }

    private void LogResult(IReadOnlyList<string> arguments, string workingDirectory, TfCommandResult result, long elapsedMilliseconds)
    {
        if (_logger is null)
        {
            return;
        }

        string message = string.Format(
            CultureInfo.CurrentCulture,
            TfvcStrings.LogTfCommandFinishedFormat,
            arguments.Count > 0 ? arguments[0] : string.Empty,
            result.ExitCode,
            elapsedMilliseconds,
            workingDirectory);
        if (result.Succeeded)
        {
            _logger.Info(message);
            return;
        }

        string error = result.StandardError.Trim();
        int lineEnd = error.IndexOfAny(['\r', '\n']);
        _logger.Warn(message + ": " + (lineEnd < 0 ? error : error.Substring(0, lineEnd)));
    }

    // tf.exe writes redirected output in the ANSI code page of the system (GetACP), not in the OEM code page. The ANSI
    // code page follows the system locale, which can differ from the user's format culture, and is 65001 when Windows
    // uses UTF-8 for non-Unicode programs. With the code pages provider registered, code page 0 selects exactly that
    // code page; without a system code page (other systems) it falls back to UTF-8.
    internal static Encoding CreateOutputEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(0);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
