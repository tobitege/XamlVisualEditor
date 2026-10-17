using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using ReactiveUI;

namespace XamlVisualEditor.SourceControl.UI.Behaviors;

/// <summary>Handles a view model interaction that places text on the clipboard of the control's top level.</summary>
public sealed class ClipboardInteractionBehavior
{
    /// <summary>Interaction whose input text is copied to the clipboard.</summary>
    public static readonly AttachedProperty<Interaction<string, RxVoid>?> InteractionProperty =
        AvaloniaProperty.RegisterAttached<ClipboardInteractionBehavior, Control, Interaction<string, RxVoid>?>("Interaction");

    private static readonly AttachedProperty<IDisposable?> RegistrationProperty =
        AvaloniaProperty.RegisterAttached<ClipboardInteractionBehavior, Control, IDisposable?>("Registration");

    static ClipboardInteractionBehavior()
    {
        InteractionProperty.Changed.AddClassHandler<Control>(OnInteractionChanged);
    }

    /// <summary>Gets the interaction of a control.</summary>
    public static Interaction<string, RxVoid>? GetInteraction(Control control) => control.GetValue(InteractionProperty);

    /// <summary>Sets the interaction of a control.</summary>
    public static void SetInteraction(Control control, Interaction<string, RxVoid>? value) => control.SetValue(InteractionProperty, value);

    private static void OnInteractionChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        control.GetValue(RegistrationProperty)?.Dispose();
        control.SetValue(RegistrationProperty, null);
        if (e.NewValue is not Interaction<string, RxVoid> interaction)
        {
            return;
        }

        IDisposable registration = interaction.RegisterHandler(async context =>
        {
            IClipboard? clipboard = TopLevel.GetTopLevel(control)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(context.Input);
            }

            context.SetOutput(RxVoid.Default);
        });
        control.SetValue(RegistrationProperty, registration);
    }
}
