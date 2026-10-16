using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace XamlVisualEditor.Designer.DragDrop;

/// <summary>Starts a namespace-preserving drag from a toolbox list without code-behind handlers.</summary>
public sealed class ToolboxDragBehavior
{
    /// <summary>Enables control dragging on a toolbox list.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ToolboxDragBehavior, Control, bool>("IsEnabled");
    private static readonly AttachedProperty<DragCandidate?> CandidateProperty =
        AvaloniaProperty.RegisterAttached<ToolboxDragBehavior, Control, DragCandidate?>("Candidate");

    static ToolboxDragBehavior() => IsEnabledProperty.Changed.AddClassHandler<Control>(EnabledChanged);

    /// <summary>Gets whether dragging is enabled.</summary>
    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    /// <summary>Sets whether dragging is enabled.</summary>
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    private static void EnabledChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is true)
        {
            control.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel, true);
            control.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel, true);
            control.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel, true);
        }
        else
        {
            control.RemoveHandler(InputElement.PointerPressedEvent, Pressed);
            control.RemoveHandler(InputElement.PointerMovedEvent, Moved);
            control.RemoveHandler(InputElement.PointerReleasedEvent, Released);
            control.SetValue(CandidateProperty, null);
        }
    }

    private static void Pressed(object? sender, PointerPressedEventArgs args)
    {
        if (sender is not Control owner || !args.GetCurrentPoint(owner).Properties.IsLeftButtonPressed) return;
        Control? source = args.Source as Control;
        IToolboxDragItem? item = source?.DataContext as IToolboxDragItem
            ?? source?.GetVisualAncestors().OfType<Control>().Select(control => control.DataContext).OfType<IToolboxDragItem>().FirstOrDefault();
        if (item?.CanDrag != true) return;
        owner.SetValue(CandidateProperty, new DragCandidate(args.GetPosition(owner), args, item));
    }

    private static async void Moved(object? sender, PointerEventArgs args)
    {
        if (sender is not Control owner || owner.GetValue(CandidateProperty) is not { } candidate) return;
        if (!args.GetCurrentPoint(owner).Properties.IsLeftButtonPressed)
        {
            owner.SetValue(CandidateProperty, null);
            return;
        }
        Vector movement = args.GetPosition(owner) - candidate.Start;
        if (movement.Length < 4) return;
        owner.SetValue(CandidateProperty, null);
        DataTransfer transfer = new();
        transfer.Add(DataTransferItem.Create(DesignerDataFormats.ToolboxItem,
            new ToolboxDragData(candidate.Item.TypeName, candidate.Item.XmlNamespace).Serialize()));
        try
        {
            await Avalonia.Input.DragDrop.DoDragDropAsync(candidate.Trigger, transfer, DragDropEffects.Copy);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Toolbox drag failed: {0}", error);
        }
        finally
        {
            args.Pointer.Capture(null);
        }
    }

    private static void Released(object? sender, PointerReleasedEventArgs args)
    {
        if (sender is Control owner) owner.SetValue(CandidateProperty, null);
    }

    private sealed record DragCandidate(Point Start, PointerPressedEventArgs Trigger, IToolboxDragItem Item);
}
