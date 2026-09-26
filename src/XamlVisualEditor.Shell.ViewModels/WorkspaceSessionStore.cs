using System.Text.Json;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>The state of a workspace that is restored when it opens again: startup project and open documents.</summary>
public sealed record WorkspaceSession
{
    /// <summary>Gets the project file of the startup project.</summary>
    public string? StartupProjectPath { get; init; }

    /// <summary>Gets the target framework of the startup project, for projects with several target frameworks.</summary>
    public string? StartupTargetFramework { get; init; }

    /// <summary>Gets the open documents in tab order.</summary>
    public IReadOnlyList<string> OpenDocuments { get; init; } = Array.Empty<string>();

    /// <summary>Gets the document that was active.</summary>
    public string? ActiveDocument { get; init; }
}

/// <summary>
/// Persists one <see cref="WorkspaceSession"/> per workspace, most recently used first. The session is independent
/// of the dock layout, which stores the arrangement of the panels only.
/// </summary>
public sealed class WorkspaceSessionStore
{
    private const int MaxWorkspaces = 50;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly string? _path;
    private List<WorkspaceSessionEntry> _entries;

    /// <summary>Creates the store; without a path the sessions stay in memory.</summary>
    public WorkspaceSessionStore(string? path = null)
    {
        _path = path;
        _entries = Read(path);
    }

    /// <summary>Gets the saved session of a workspace, or null.</summary>
    public WorkspaceSession? Load(string workspacePath) =>
        _entries.FirstOrDefault(entry => FileSystemPathComparison.Equals(entry.WorkspacePath, workspacePath))?.Session;

    /// <summary>Saves the session of a workspace and moves it to the front.</summary>
    public void Save(string workspacePath, WorkspaceSession session)
    {
        List<WorkspaceSessionEntry> entries = new(Math.Min(_entries.Count + 1, MaxWorkspaces)) { new(workspacePath, session) };
        entries.AddRange(_entries
            .Where(entry => !FileSystemPathComparison.Equals(entry.WorkspacePath, workspacePath))
            .Take(MaxWorkspaces - 1));
        _entries = entries;
        if (_path is null)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, WriteOptions));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The session is a convenience: a failed write keeps the editor working with the in-memory state.
        }
    }

    private static List<WorkspaceSessionEntry> Read(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new List<WorkspaceSessionEntry>();
        }

        try
        {
            List<WorkspaceSessionEntry>? entries = JsonSerializer.Deserialize<List<WorkspaceSessionEntry>>(SharedFileReader.ReadAllText(path));
            return entries?.Where(entry => !string.IsNullOrWhiteSpace(entry.WorkspacePath) && entry.Session is not null).ToList()
                ?? new List<WorkspaceSessionEntry>();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new List<WorkspaceSessionEntry>();
        }
    }

    internal sealed record WorkspaceSessionEntry(string WorkspacePath, WorkspaceSession Session);
}
