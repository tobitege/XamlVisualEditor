using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using RemoteInput = Avalonia.Remote.Protocol.Input;

namespace XamlVisualEditor.App.Views;

public sealed partial class PreviewerSurfaceControl
{
    private IPointer? _previewPointer;
    private readonly HashSet<RemoteInput.MouseButton> _previewButtons = new();
    private readonly Dictionary<Key, RemoteInput.KeyEventMessage> _previewKeys = new();

    private bool CanPreviewInput => _document is { IsDisposed: false, UseExternalPreviewer: true } document
        && !document.DesignSurface.IsEditMode && _isConnected && !_hasError
        && _lastFrame is { Width: > 0, Height: > 0 } && Bounds.Width > 0 && Bounds.Height > 0;

    private async void SendPreviewInput(RemoteInput.InputEventMessageBase message)
    {
        var session = _session;
        var connection = session?.Connection;
        if (connection is null) return;
        try { await connection.Send(message); }
        catch (Exception error)
        {
            if (ReferenceEquals(_session, session) && ReferenceEquals(session!.Connection, connection))
                session.ReportError($"Preview input failed: {error.Message}");
        }
    }

    private void SendPreviewPointer(PointerEventArgs args, RemoteInput.PointerEventMessageBase message)
    {
        Point position = args.GetPosition(this);
        message.X = position.X * (_naturalWidth ?? Bounds.Width / PreviewZoom) / Bounds.Width;
        message.Y = position.Y * (_naturalHeight ?? Bounds.Height / PreviewZoom) / Bounds.Height;
        message.Modifiers = PreviewModifiers(args.KeyModifiers, args.GetCurrentPoint(this).Properties);
        SendPreviewInput(message);
        args.Handled = true;
    }

    private bool ForwardPreviewPress(PointerPressedEventArgs args)
    {
        if (!CanPreviewInput) return false;
        RemoteInput.MouseButton button = args.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => RemoteInput.MouseButton.Left,
            PointerUpdateKind.MiddleButtonPressed => RemoteInput.MouseButton.Middle,
            PointerUpdateKind.RightButtonPressed => RemoteInput.MouseButton.Right,
            _ => RemoteInput.MouseButton.None
        };
        if (button == RemoteInput.MouseButton.None) return false;
        Focus();
        _previewPointer = args.Pointer;
        _previewButtons.Add(button);
        args.Pointer.Capture(this);
        SendPreviewPointer(args, new RemoteInput.PointerPressedEventMessage { Button = button });
        return true;
    }

    private bool ForwardPreviewMove(PointerEventArgs args)
    {
        if (!CanPreviewInput) return false;
        SendPreviewPointer(args, new RemoteInput.PointerMovedEventMessage());
        return true;
    }

    private bool ForwardPreviewRelease(PointerReleasedEventArgs args)
    {
        if (!CanPreviewInput) return false;
        RemoteInput.MouseButton button = args.InitialPressMouseButton switch
        {
            MouseButton.Left => RemoteInput.MouseButton.Left,
            MouseButton.Middle => RemoteInput.MouseButton.Middle,
            MouseButton.Right => RemoteInput.MouseButton.Right,
            _ => RemoteInput.MouseButton.None
        };
        if (!_previewButtons.Remove(button)) return false;
        SendPreviewPointer(args, new RemoteInput.PointerReleasedEventMessage { Button = button });
        if (_previewButtons.Count == 0)
        {
            _previewPointer = null;
            args.Pointer.Capture(null);
        }
        return true;
    }

    protected override void OnPointerExited(PointerEventArgs args)
    {
        base.OnPointerExited(args);
        ForwardPreviewMove(args);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs args)
    {
        base.OnPointerWheelChanged(args);
        if (CanPreviewInput)
            SendPreviewPointer(args, new RemoteInput.ScrollEventMessage { DeltaX = args.Delta.X, DeltaY = args.Delta.Y });
    }

    private bool ForwardPreviewKey(KeyEventArgs args, bool isDown)
    {
        if (!CanPreviewInput) return false;
        RemoteInput.KeyEventMessage message = new()
        {
            IsDown = isDown, Key = (RemoteInput.Key)args.Key,
            PhysicalKey = (RemoteInput.PhysicalKey)args.PhysicalKey, KeySymbol = args.KeySymbol,
            Modifiers = PreviewModifiers(args.KeyModifiers)
        };
        if (isDown) _previewKeys[args.Key] = message;
        else _previewKeys.Remove(args.Key);
        SendPreviewInput(message);
        args.Handled = true;
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs args)
    {
        base.OnKeyUp(args);
        ForwardPreviewKey(args, false);
    }

    protected override void OnTextInput(TextInputEventArgs args)
    {
        base.OnTextInput(args);
        if (!CanPreviewInput) return;
        SendPreviewInput(new RemoteInput.TextInputEventMessage { Text = args.Text ?? string.Empty });
        args.Handled = true;
    }

    protected override void OnLostFocus(FocusChangedEventArgs args)
    {
        base.OnLostFocus(args);
        ReleasePreviewInput();
    }

    private void ReleasePreviewInput()
    {
        IPointer? pointer = _previewPointer;
        _previewPointer = null;
        RemoteInput.MouseButton[] buttons = _previewButtons.ToArray();
        RemoteInput.KeyEventMessage[] keys = _previewKeys.Values.ToArray();
        _previewButtons.Clear();
        _previewKeys.Clear();
        if (buttons.Length > 0)
            SendPreviewInput(new RemoteInput.PointerMovedEventMessage { X = -1, Y = -1 });
        foreach (RemoteInput.MouseButton button in buttons)
            SendPreviewInput(new RemoteInput.PointerReleasedEventMessage { Button = button, X = -1, Y = -1 });
        foreach (RemoteInput.KeyEventMessage key in keys)
            SendPreviewInput(new RemoteInput.KeyEventMessage
            {
                IsDown = false, Key = key.Key, PhysicalKey = key.PhysicalKey, KeySymbol = key.KeySymbol
            });
        pointer?.Capture(null);
    }

    private static RemoteInput.InputModifiers[] PreviewModifiers(KeyModifiers keys, PointerPointProperties? pointer = null)
    {
        List<RemoteInput.InputModifiers> modifiers = new(7);
        if (keys.HasFlag(KeyModifiers.Alt)) modifiers.Add(RemoteInput.InputModifiers.Alt);
        if (keys.HasFlag(KeyModifiers.Control)) modifiers.Add(RemoteInput.InputModifiers.Control);
        if (keys.HasFlag(KeyModifiers.Shift)) modifiers.Add(RemoteInput.InputModifiers.Shift);
        if (keys.HasFlag(KeyModifiers.Meta)) modifiers.Add(RemoteInput.InputModifiers.Windows);
        if (pointer?.IsLeftButtonPressed == true) modifiers.Add(RemoteInput.InputModifiers.LeftMouseButton);
        if (pointer?.IsRightButtonPressed == true) modifiers.Add(RemoteInput.InputModifiers.RightMouseButton);
        if (pointer?.IsMiddleButtonPressed == true) modifiers.Add(RemoteInput.InputModifiers.MiddleMouseButton);
        return modifiers.ToArray();
    }
}
