using System;
using System.Reactive;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using ReactiveUI;
using XamlVisualEditor.IdeBridgeExtension;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.IdeBridgeExtension.Views;

public sealed partial class IdeBridgePanelView : UserControl
{
    private IDisposable? _clipboardHandler;

    public IdeBridgePanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RegisterHandlers();
        DetachedFromVisualTree += (_, _) => DisposeHandlers();
    }

    private void RegisterHandlers()
    {
        DisposeHandlers();

        if (DataContext is not IdeBridgePanelViewModel vm)
        {
            return;
        }

        _clipboardHandler = vm.CopyToClipboardInteraction.RegisterHandler(async interaction =>
        {
            IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(interaction.Input);
            }

            interaction.SetOutput(RxVoid.Default);
        });
    }

    private void DisposeHandlers()
    {
        _clipboardHandler?.Dispose();
        _clipboardHandler = null;
    }
}
