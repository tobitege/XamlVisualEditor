using System.Text.Json;
using System.Text.Json.Serialization;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Core;

namespace XamlVisualEditor.Shell.ViewModels;

public enum TerminalLocation { Internal, External }
public enum TerminalShell { CommandPrompt, WindowsPowerShell, PowerShell7 }

/// <summary>Gespeicherte Sprachwahl und Windows-Terminalprofile.</summary>
public sealed record EditorOptions
{
    public EditorThemeSelection EditorTheme { get; init; } = EditorThemeSelection.Default;
    public string Language { get; init; } = "en";
    /// <summary>Opens the most recent workspace or folder at startup when no path is passed on the command line.</summary>
    public bool RestoreLastWorkspace { get; init; }
    /// <summary>Lists folders before files in the File Explorer, also when it sorts by its column header; otherwise both sort together by name.</summary>
    public bool FileExplorerFoldersBeforeFiles { get; init; } = true;
    /// <summary>Shows the full path of the active document in the window title instead of its file name.</summary>
    public bool ShowFullPathInTitle { get; init; }
    /// <summary>Grid, ruler and coordinate strip settings that every designer document starts with.</summary>
    public DesignerViewportOptions DesignerViewport { get; init; } = new();
    public TerminalLocation TerminalLocation { get; init; }
    public TerminalShell TerminalShell { get; init; }
    public string CommandPromptPath { get; init; } = Environment.GetEnvironmentVariable("ComSpec")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
    public string WindowsPowerShellPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    public string PowerShell7Path { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");

    [JsonIgnore]
    public string ShellPath => TerminalShell switch
    {
        TerminalShell.CommandPrompt => CommandPromptPath,
        TerminalShell.WindowsPowerShell => WindowsPowerShellPath,
        TerminalShell.PowerShell7 => PowerShell7Path,
        _ => throw new InvalidOperationException("Unknown terminal shell.")
    };
}

/// <summary>Stored viewport settings of the designer; a change in one document applies to all designer documents.</summary>
public sealed record DesignerViewportOptions
{
    public bool ShowGrid { get; init; }
    public bool SnapToGrid { get; init; } = true;
    /// <summary>Grid size in pixels for the visible grid and for snapping.</summary>
    public double GridSize { get; init; } = 8;
    public bool ShowRulers { get; init; } = true;
    public bool ShowCoordinateStrip { get; init; } = true;

    /// <summary>Reads the settings of a design surface.</summary>
    public static DesignerViewportOptions From(DesignSurfaceViewModel surface) => new()
    {
        ShowGrid = surface.ShowGrid,
        SnapToGrid = surface.SnapToGrid,
        GridSize = surface.GridSnapSize,
        ShowRulers = surface.ShowRulers,
        ShowCoordinateStrip = surface.ShowCoordinateStrip
    };

    /// <summary>Applies the settings to a design surface.</summary>
    public void ApplyTo(DesignSurfaceViewModel surface)
    {
        surface.ShowGrid = ShowGrid;
        surface.SnapToGrid = SnapToGrid;
        surface.GridSnapSize = GridSize;
        surface.ShowRulers = ShowRulers;
        surface.ShowCoordinateStrip = ShowCoordinateStrip;
    }

    /// <summary>Replaces a missing file entry with the defaults and limits the grid size to the offered range.</summary>
    public static DesignerViewportOptions Normalize(DesignerViewportOptions? options)
    {
        DesignerViewportOptions value = options ?? new();
        double gridSize = double.IsFinite(value.GridSize)
            ? Math.Clamp(Math.Round(value.GridSize), DesignSurfaceViewModel.MinimumGridSize, DesignSurfaceViewModel.MaximumGridSize)
            : new DesignerViewportOptions().GridSize;
        return value with { GridSize = gridSize };
    }
}

/// <summary>Ohne Dateipfad bleiben Optionen im Speicher, insbesondere in Tests.</summary>
public sealed class EditorOptionsStore
{
    private readonly string? _path;
    public EditorOptions Current { get; private set; } = new();
    public string? LoadError { get; }

    public EditorOptionsStore(string? path = null)
    {
        _path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            EditorOptions loaded = JsonSerializer.Deserialize<EditorOptions>(SharedFileReader.ReadAllText(path)) ?? new();
            if (loaded.Language is not ("en" or "de") || !Enum.IsDefined(loaded.TerminalLocation) || !Enum.IsDefined(loaded.TerminalShell))
                throw new JsonException("Unsupported language or terminal option.");
            if (loaded.EditorTheme is null || string.IsNullOrWhiteSpace(loaded.EditorTheme.ProviderId) || string.IsNullOrWhiteSpace(loaded.EditorTheme.ThemeId))
                throw new JsonException("Invalid editor theme selection.");
            Current = loaded with { DesignerViewport = DesignerViewportOptions.Normalize(loaded.DesignerViewport) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = error.Message;
        }
    }

    public void Save(EditorOptions options)
    {
        if (_path is not null)
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }));
        }
        Current = options;
    }
}
