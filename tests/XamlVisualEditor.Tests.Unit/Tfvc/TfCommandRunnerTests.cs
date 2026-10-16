using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

/// <summary>Runs <see cref="TfCommandRunner"/> against cmd.exe in place of tf.exe.</summary>
public sealed class TfCommandRunnerTests
{
    private const string WINDOWS_ONLY = "The runner starts cmd.exe and reads the ANSI code page of Windows.";
    private const string MARKER_FILE = "marker.txt";

    [Fact]
    public async Task RunAsync_GrandchildFinishesWithoutTimeout()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WINDOWS_ONLY);
        using TemporaryFolder folder = new();

        TfCommandResult result = await CreateRunner().RunAsync(folder.Path, CreateGrandchildArguments(pingCount: 2), TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(folder.Path, MARKER_FILE)));
    }

    [Fact]
    public async Task RunAsync_TimeoutKillsProcessTree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WINDOWS_ONLY);
        using TemporaryFolder folder = new();

        await Assert.ThrowsAsync<TimeoutException>(
            () => CreateRunner().RunAsync(folder.Path, CreateGrandchildArguments(pingCount: 6), TimeSpan.FromSeconds(1), CancellationToken.None));

        // A surviving grandchild writes the marker about four seconds after the kill.
        await Task.Delay(TimeSpan.FromSeconds(8));
        Assert.False(File.Exists(Path.Combine(folder.Path, MARKER_FILE)));
    }

    [Fact]
    public async Task RunAsync_CancellationKillsProcessTree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WINDOWS_ONLY);
        using TemporaryFolder folder = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateRunner().RunAsync(folder.Path, CreateGrandchildArguments(pingCount: 6), TimeSpan.FromSeconds(60), cancellation.Token));

        await Task.Delay(TimeSpan.FromSeconds(8));
        Assert.False(File.Exists(Path.Combine(folder.Path, MARKER_FILE)));
    }

    [Fact]
    public void CreateOutputEncoding_UsesAnsiCodePageOfTheSystem()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WINDOWS_ONLY);

        Assert.Equal((int)GetACP(), TfCommandRunner.CreateOutputEncoding().CodePage);
    }

    [Fact]
    public async Task RunAsync_DecodesOutputInAnsiCodePageOfTheSystem()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WINDOWS_ONLY);
        const string text = "Änderungssatz für Grüße";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding ansi = Encoding.GetEncoding((int)GetACP());
        byte[] bytes = ansi.GetBytes(text);
        Assert.SkipUnless(ansi.GetString(bytes) == text, "The ANSI code page of the system cannot represent the test text.");
        using TemporaryFolder folder = new();
        File.WriteAllBytes(Path.Combine(folder.Path, "output.txt"), bytes);

        // type writes the bytes of a file without a Unicode byte order mark unchanged, like tf.exe writes ANSI text.
        TfCommandResult result = await CreateRunner().RunAsync(folder.Path, ["/c", "type", "output.txt"], TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(text, result.StandardOutput.Trim());
    }

    private static TfCommandRunner CreateRunner()
    {
        return new TfCommandRunner(new FixedLocator(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
    }

    // cmd.exe starts a second cmd.exe that waits about pingCount - 1 seconds and then writes the marker file into the
    // working directory. The carets keep the outer cmd.exe from interpreting the redirections and the command separator.
    private static IReadOnlyList<string> CreateGrandchildArguments(int pingCount)
    {
        return ["/c", "cmd", "/c", "ping", "-n", pingCount.ToString(CultureInfo.InvariantCulture), "127.0.0.1", "^>nul", "^&", "echo", "done^>" + MARKER_FILE];
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    private sealed class FixedLocator : ITfExeLocator
    {
        private readonly string _path;

        public FixedLocator(string path)
        {
            _path = path;
        }

        public string? Locate(string? configuredPath) => _path;
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tf-runner-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
