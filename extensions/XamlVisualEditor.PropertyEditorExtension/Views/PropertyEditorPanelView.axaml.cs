using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.PropertyEditorExtension.Views;

public sealed partial class PropertyEditorPanelView : UserControl
{
    private IDisposable? _structureInteraction;

    private void OnEventEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: EventEntryViewModel entry } || entry.IsApplying)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            entry.CommitCommand.Execute().Subscribe();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            entry.CancelEditCommand.Execute().Subscribe();
        }
    }

    private void OnEventEditorLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: EventEntryViewModel entry } && !entry.IsApplying)
        {
            entry.CommitCommand.Execute().Subscribe();
        }
    }

    public PropertyEditorPanelView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => RegisterStructureEditor();
        AttachedToVisualTree += (_, _) => RegisterStructureEditor();
        DetachedFromVisualTree += (_, _) =>
        {
            _structureInteraction?.Dispose();
            _structureInteraction = null;
        };
    }

    private void RegisterStructureEditor()
    {
        _structureInteraction?.Dispose();
        _structureInteraction = null;
        if (DataContext is not PropertyEditorPanelViewModel viewModel) return;
        _structureInteraction = viewModel.EditStructureInteraction.RegisterHandler(async context =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner)
                throw new InvalidOperationException(UiStrings.StructureEditorRequiresWindow);
            PropertyStructureEditorViewModel editor = context.Input;
            Window dialog = new()
            {
                Title = editor.Title, Width = 860, Height = 720, MinWidth = 600, MinHeight = 480,
                Classes = { "editor-chrome" },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new PropertyStructureEditorView { DataContext = editor }
            };
            void Close() => dialog.Close();
            editor.CloseRequested += Close;
            dialog.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Escape && !editor.IsBusy) { args.Handled = true; dialog.Close(); }
            };
            try { await dialog.ShowDialog(owner); }
            finally { editor.CloseRequested -= Close; }
            context.SetOutput(RxVoid.Default);
        });
    }
}
