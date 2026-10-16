using System;
using System.Reactive;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.OutputExtension.Views;

public sealed partial class ProblemsPanelView : UserControl
{
    public static FuncValueConverter<string, string> FilterLabelConverter { get; } =
        new(value => value is null ? string.Empty : UiLabels.DiagnosticFilter(value));

    private IDisposable? _clipboardHandler;

    public ProblemsPanelView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => RegisterHandlers();
        AttachedToVisualTree += (_, _) => RegisterHandlers();
        DetachedFromVisualTree += (_, _) => DisposeHandlers();
    }

    private void RegisterHandlers()
    {
        DisposeHandlers();

        if (DataContext is not ProblemsPanelViewModel vm)
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
