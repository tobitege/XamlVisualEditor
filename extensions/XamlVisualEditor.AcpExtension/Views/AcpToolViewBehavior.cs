using System;
using System.Diagnostics;
using System.Reactive;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Xaml.Interactivity;
using Avalonia.Threading;
using ReactiveUI;
using XamlVisualEditor.Acp;

namespace XamlVisualEditor.AcpExtension.Views;

public sealed class AcpToolViewBehavior : Behavior<AcpToolView>
{
    private IDisposable? _clipboardHandler;
    private IDisposable? _openUrlHandler;
    private IDisposable? _permissionHandler;
    private AcpPermissionDialog? _permissionDialog;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject!.DataContextChanged += OnDataContextChanged;
        AssociatedObject.AttachedToVisualTree += OnAttachedToVisualTree;
        AssociatedObject.DetachedFromVisualTree += OnDetachedFromVisualTree;
        RegisterHandlers();
    }

    protected override void OnDetaching()
    {
        AssociatedObject!.DataContextChanged -= OnDataContextChanged;
        AssociatedObject.AttachedToVisualTree -= OnAttachedToVisualTree;
        AssociatedObject.DetachedFromVisualTree -= OnDetachedFromVisualTree;
        DisposeHandlers();
        base.OnDetaching();
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => RegisterHandlers();
    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => RegisterHandlers();
    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => DisposeHandlers();

    private void RegisterHandlers()
    {
        DisposeHandlers();
        if (AssociatedObject?.DataContext is not AcpToolViewModel vm) return;

        _clipboardHandler = vm.CopyToClipboardInteraction.RegisterHandler(async interaction =>
        {
            var clipboard = TopLevel.GetTopLevel(AssociatedObject)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(interaction.Input);
            interaction.SetOutput(Unit.Default);
        });
        _openUrlHandler = vm.OpenUrlInteraction.RegisterHandler(interaction =>
        {
            if (Uri.TryCreate(interaction.Input, UriKind.Absolute, out Uri? uri)
                && uri.Scheme is "http" or "https")
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            interaction.SetOutput(Unit.Default);
        });
        _permissionHandler = vm.PermissionInteraction.RegisterHandler(async interaction =>
        {
            if (TopLevel.GetTopLevel(AssociatedObject) is not Window owner
                || interaction.Input.CancellationToken.IsCancellationRequested)
            {
                interaction.SetOutput(AcpPermissionOutcome.Cancelled());
                return;
            }
            AcpPermissionDialog dialog = new() { DataContext = new AcpPermissionDialogViewModel(interaction.Input) };
            _permissionDialog = dialog;
            using CancellationTokenRegistration cancellation = interaction.Input.CancellationToken.Register(() =>
                Dispatcher.UIThread.Post(() => dialog.Close(AcpPermissionOutcome.Cancelled())));
            try
            {
                AcpPermissionOutcome? result = await dialog.ShowDialog<AcpPermissionOutcome>(owner);
                interaction.SetOutput(result ?? AcpPermissionOutcome.Cancelled());
            }
            finally { _permissionDialog = null; }
        });
    }

    private void DisposeHandlers()
    {
        _permissionDialog?.Close(AcpPermissionOutcome.Cancelled());
        _permissionDialog = null;
        _clipboardHandler?.Dispose();
        _openUrlHandler?.Dispose();
        _permissionHandler?.Dispose();
        _clipboardHandler = _openUrlHandler = _permissionHandler = null;
    }
}
