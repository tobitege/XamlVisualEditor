using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit;
using XamlVisualEditor.App.Services;

namespace XamlVisualEditor.App.Views;

/// <summary>Connects an editor with the text editor view host: view options and the extension context menu.</summary>
public sealed class TextEditorFeaturesBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextEditorFeaturesBehavior, TextEditor, bool>("IsEnabled");

    private static readonly AttachedProperty<TextEditorViewHost?> HostProperty =
        AvaloniaProperty.RegisterAttached<TextEditorFeaturesBehavior, TextEditor, TextEditorViewHost?>("Host");

    static TextEditorFeaturesBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextEditor>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(TextEditor editor)
    {
        return editor.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(TextEditor editor, bool value)
    {
        editor.SetValue(IsEnabledProperty, value);
    }

    private static void OnIsEnabledChanged(TextEditor editor, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            editor.AttachedToVisualTree += OnAttachedToVisualTree;
            editor.DetachedFromVisualTree += OnDetachedFromVisualTree;
            if (TopLevel.GetTopLevel(editor) is not null)
            {
                Attach(editor);
            }

            return;
        }

        editor.AttachedToVisualTree -= OnAttachedToVisualTree;
        editor.DetachedFromVisualTree -= OnDetachedFromVisualTree;
        Detach(editor);
    }

    private static void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor)
        {
            Attach(editor);
        }
    }

    private static void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor)
        {
            Detach(editor);
        }
    }

    private static void Attach(TextEditor editor)
    {
        if (editor.GetValue(HostProperty) is not null
            || !editor.TryFindResource(TextEditorViewHost.RESOURCE_KEY, out object? resource)
            || resource is not TextEditorViewHost host)
        {
            return;
        }

        editor.SetValue(HostProperty, host);
        host.Register(editor);
        editor.DataContextChanged += OnDataContextChanged;

        ContextMenu menu = new() { ItemsSource = host.ContextMenuItems };
        menu.Classes.Add("editor-chrome");
        menu.Classes.Add("editor-context");
        menu.Opening += OnContextMenuOpening;
        editor.ContextMenu = menu;
    }

    private static void Detach(TextEditor editor)
    {
        if (editor.GetValue(HostProperty) is not { } host)
        {
            return;
        }

        editor.DataContextChanged -= OnDataContextChanged;
        if (editor.ContextMenu is { } menu && menu.Classes.Contains("editor-context"))
        {
            menu.Opening -= OnContextMenuOpening;
            editor.ContextMenu = null;
        }

        host.Unregister(editor);
        editor.ClearValue(HostProperty);
    }

    private static void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is TextEditor editor && editor.GetValue(HostProperty) is { } host)
        {
            host.Apply(editor);
        }
    }

    // An empty menu, for example without the text editing extension, does not open.
    private static void OnContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is ContextMenu { ItemCount: 0 })
        {
            e.Cancel = true;
        }
    }
}
