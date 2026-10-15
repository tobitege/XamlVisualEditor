using System;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Data.Converters;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Reads the file commands of a document tab from the dockable in the data context of its menu items. The menus exist
/// before any tab opens them, so the dockable can be missing or show no file; then there is nothing to show.
/// </summary>
public static class DocumentTabMenuConverters
{
    /// <summary>Returns whether the dockable offers file commands.</summary>
    public static readonly IValueConverter HasFileCommands =
        new FuncValueConverter<object?, bool>(dockable => GetCommands(dockable) is not null);

    /// <summary>Returns the full path of the file of the dockable.</summary>
    public static readonly IValueConverter FilePath =
        new FuncValueConverter<object?, string?>(dockable => (dockable as IFileDockable)?.FilePath);

    /// <summary>Returns the file command that the converter parameter names.</summary>
    public static readonly IValueConverter Command = new CommandConverter();

    internal static ICommand? GetCommand(object? dockable, string? name) => GetCommands(dockable) is { } commands
        ? name switch
        {
            nameof(DocumentTabCommands.RevealInFileExplorerCommand) => commands.RevealInFileExplorerCommand,
            nameof(DocumentTabCommands.RevealInSolutionExplorerCommand) => commands.RevealInSolutionExplorerCommand,
            nameof(DocumentTabCommands.CopyPathCommand) => commands.CopyPathCommand,
            nameof(DocumentTabCommands.CopyFileNameCommand) => commands.CopyFileNameCommand,
            _ => null
        }
        : null;

    private static DocumentTabCommands? GetCommands(object? dockable) => (dockable as IFileDockable)?.TabCommands;

    private sealed class CommandConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            GetCommand(value, parameter as string);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
