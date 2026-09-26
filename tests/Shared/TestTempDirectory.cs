using System;
using System.IO;

namespace XamlVisualEditor.Tests.Shared;

/// <summary>
/// Gives each test process its own temporary folder below <c>%TEMP%\xve-tests</c> and removes it when the
/// process ends. <see cref="Path.GetTempPath"/> reads TMP, TEMP and TMPDIR on every call, so files that tests
/// and the processes they start create in the temporary folder no longer accumulate in the user's one.
/// </summary>
internal static class TestTempDirectory
{
    // Folders of test processes that were killed stay behind; they are removed by a later run.
    private static readonly TimeSpan StaleAge = TimeSpan.FromHours(12);
    private const string RootVariable = "XVE_TEST_TEMP_ROOT";

    /// <summary>Gets the temporary folder of this test process.</summary>
    public static string Root { get; private set; } = string.Empty;

    /// <summary>Redirects the temporary folder of this process; call once per process before any test runs.</summary>
    public static void Initialize()
    {
        // Another load of a test assembly and the processes that tests start keep the folder of the first
        // initialization instead of nesting a new one inside it.
        if (Environment.GetEnvironmentVariable(RootVariable) is { Length: > 0 } inherited && Directory.Exists(inherited))
        {
            Root = inherited;
            return;
        }

        string parent = Path.Combine(Path.GetTempPath(), "xve-tests");
        RemoveStaleRuns(parent);
        Root = Path.Combine(parent, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        string rootWithSeparator = Root + Path.DirectorySeparatorChar;
        Environment.SetEnvironmentVariable("TMP", rootWithSeparator);
        Environment.SetEnvironmentVariable("TEMP", rootWithSeparator);
        Environment.SetEnvironmentVariable("TMPDIR", rootWithSeparator);
        Environment.SetEnvironmentVariable(RootVariable, Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(Root);
    }

    private static void RemoveStaleRuns(string parent)
    {
        if (!Directory.Exists(parent))
        {
            return;
        }

        DateTime limit = DateTime.UtcNow - StaleAge;
        foreach (string directory in Directory.EnumerateDirectories(parent))
        {
            if (Directory.GetLastWriteTimeUtc(directory) < limit) TryDelete(directory);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A process started by a test may still hold a file; the next run removes the folder once it is stale.
        }
    }
}
