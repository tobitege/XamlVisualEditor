using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace XamlVisualEditor.TfvcExtension.Views;

/// <summary>Executes a command when the attached control is double-tapped.</summary>
public sealed class DoubleTapCommandBehavior
{
    /// <summary>Command executed on a double tap.</summary>
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<DoubleTapCommandBehavior, Control, ICommand?>("Command");

    static DoubleTapCommandBehavior()
    {
        CommandProperty.Changed.AddClassHandler<Control>(OnCommandChanged);
    }

    /// <summary>Gets the command of a control.</summary>
    public static ICommand? GetCommand(Control control) => control.GetValue(CommandProperty);

    /// <summary>Sets the command of a control.</summary>
    public static void SetCommand(Control control, ICommand? value) => control.SetValue(CommandProperty, value);

    private static void OnCommandChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        control.RemoveHandler(InputElement.DoubleTappedEvent, OnDoubleTapped);
        if (e.NewValue is ICommand)
        {
            control.AddHandler(InputElement.DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && GetCommand(control) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
