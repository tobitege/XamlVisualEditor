using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.App.Services;

/// <summary>Uses the clipboard belonging to the desktop window.</summary>
public sealed class DesktopClipboardService : IClipboardService
{
    public Task<string?> GetTextAsync() => GetClipboard().TryGetTextAsync();

    public Task SetTextAsync(string text) => GetClipboard().SetTextAsync(text);

    private static IClipboard GetClipboard() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard
        ?? throw new InvalidOperationException("The system clipboard is unavailable.");
}
