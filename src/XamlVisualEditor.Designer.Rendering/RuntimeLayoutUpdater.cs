using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Designer.Rendering;

/// <summary>Übernimmt Layout-Werte ohne XAML-Kompilierung und ohne Austausch vorhandener Controls.</summary>
internal static class RuntimeLayoutUpdater
{
    public static bool TryApply(IReadOnlyList<DesignerControlUpdate> updates, IReadOnlyDictionary<Control, Guid> identities)
    {
        List<(Action Apply, Action Undo)> changes = new();
        foreach (DesignerControlUpdate update in updates)
        {
            Control[] targets = identities.Where(pair => pair.Value == update.NodeId).Select(pair => pair.Key).ToArray();
            if (targets.Length == 0) return false;
            foreach (Control target in targets)
            {
                if (!TryPrepare(target, update.Xaml, update.ClearedProperties, out Action apply)
                    || !TryPrepare(target, update.PreviousXaml, update.PreviouslyUnsetProperties, out Action undo)) return false;
                changes.Add((apply, undo));
            }
        }
        int applied = 0;
        try
        {
            foreach (var change in changes) { applied++; change.Apply(); }
            return true;
        }
        catch
        {
            for (int index = applied - 1; index >= 0; index--) changes[index].Undo();
            return false;
        }
    }

    private static bool TryPrepare(Control control, string fragment, IReadOnlyList<string> cleared, out Action apply)
    {
        apply = static () => { };
        if (!DesignerUpdateProtocol.TryGetLayoutValues(fragment, cleared, out var values)) return false;
        List<(AvaloniaProperty Property, object? Value)> properties = new();
        try
        {
            foreach (var value in values)
            {
                AvaloniaProperty? property = value.Key switch
                {
                    "Width" => Control.WidthProperty, "Height" => Control.HeightProperty,
                    "MinWidth" => Control.MinWidthProperty, "MinHeight" => Control.MinHeightProperty,
                    "MaxWidth" => Control.MaxWidthProperty, "MaxHeight" => Control.MaxHeightProperty,
                    "Margin" => Control.MarginProperty,
                    "HorizontalAlignment" => Control.HorizontalAlignmentProperty, "VerticalAlignment" => Control.VerticalAlignmentProperty,
                    "Canvas.Left" => Canvas.LeftProperty, "Canvas.Top" => Canvas.TopProperty,
                    "Canvas.Right" => Canvas.RightProperty, "Canvas.Bottom" => Canvas.BottomProperty,
                    "Grid.Row" => Grid.RowProperty, "Grid.Column" => Grid.ColumnProperty,
                    "Grid.RowSpan" => Grid.RowSpanProperty, "Grid.ColumnSpan" => Grid.ColumnSpanProperty,
                    "DockPanel.Dock" => DockPanel.DockProperty,
                    "Spacing" when control is StackPanel => StackPanel.SpacingProperty,
                    "RowSpacing" when control is Grid => Grid.RowSpacingProperty,
                    "ColumnSpacing" when control is Grid => Grid.ColumnSpacingProperty,
                    _ => null
                };
                if (property is null) return false;
                object? parsed = value.Value is null ? null
                    : property.PropertyType == typeof(double) ? double.Parse(value.Value, CultureInfo.InvariantCulture)
                    : property.PropertyType == typeof(int) ? int.Parse(value.Value, CultureInfo.InvariantCulture)
                    : property.PropertyType == typeof(Thickness) ? Thickness.Parse(value.Value)
                    : property.PropertyType == typeof(HorizontalAlignment) ? Enum.Parse<HorizontalAlignment>(value.Value, true)
                    : property.PropertyType == typeof(VerticalAlignment) ? Enum.Parse<VerticalAlignment>(value.Value, true)
                    : Enum.Parse<Dock>(value.Value, true);
                properties.Add((property, parsed));
            }
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { return false; }
        apply = () =>
        {
            foreach (var property in properties)
            {
                if (property.Value is null) control.ClearValue(property.Property);
                else control.SetValue(property.Property, property.Value);
            }
        };
        return true;
    }
}
