using XamlVisualEditor.Terminal;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class WindowsPtyProviderTests
{
    [Fact]
    public async Task Resize_after_the_process_exited_and_the_pseudo_console_closed_is_ignored()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ConPTY is only available on Windows.");
        CancellationToken ct = TestContext.Current.CancellationToken;
        TerminalSessionOptions options = new()
        {
            Columns = 80,
            Rows = 24,
            Command = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = new[] { "/d", "/c", "exit 0" }
        };

        // ConPTY runs without a console window.
        using IPtyProcess process = new WindowsPtyProvider().StartProcess(options);

        // The output pipe reaches end of file only after the exit callback has closed the pseudo console.
        await process.Output.CopyToAsync(Stream.Null, ct).WaitAsync(TimeSpan.FromSeconds(30), ct);

        for (int step = 0; step < 10; step++)
            process.Resize(80 + step, 24 + step);
    }
}
