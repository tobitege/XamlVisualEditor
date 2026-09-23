using System.Text.Json;
using System.Text.Json.Serialization;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Shell.ViewModels;

public enum TerminalLocation { Internal, External }
public enum TerminalShell { CommandPrompt, WindowsPowerShell, PowerShell7 }

/// <summary>Gespeicherte Sprachwahl und Windows-Terminalprofile.</summary>
public sealed record EditorOptions
{
    public EditorThemeSelection EditorTheme { get; init; } = EditorThemeSelection.Default;
    public string Language { get; init; } = "en";
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
            Current = loaded;
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
