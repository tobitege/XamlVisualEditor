using System;
using System.IO;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>
/// Reports whether a file carries the read-only attribute and notifies when it changes, for example when source
/// control checks the file out.
/// </summary>
internal sealed class ReadOnlyFileMonitor : IDisposable
{
    private readonly string _filePath;
    private readonly FileSystemWatcher? _watcher;

    /// <summary>Starts monitoring; <paramref name="changed"/> runs on a background thread.</summary>
    public ReadOnlyFileMonitor(string filePath, Action changed)
    {
        _filePath = filePath;
        string? directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(filePath))
            {
                NotifyFilter = NotifyFilters.Attributes | NotifyFilters.FileName
            };
            _watcher.Changed += (_, _) => changed();
            _watcher.Created += (_, _) => changed();
            _watcher.Renamed += (_, _) => changed();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>Gets whether the file currently carries the read-only attribute.</summary>
    public bool IsReadOnly
    {
        get
        {
            try
            {
                return File.Exists(_filePath) && File.GetAttributes(_filePath).HasFlag(FileAttributes.ReadOnly);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
