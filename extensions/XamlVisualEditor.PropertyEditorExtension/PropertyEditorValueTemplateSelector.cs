using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Metadata;
using System.Windows.Input;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.PropertyEditorExtension;

public sealed class PropertyEditorValueTemplateSelector : IDataTemplate
{
    [Content]
    public IDataTemplate? TextEditor { get; set; }

    public IDataTemplate? BooleanEditor { get; set; }

    public IDataTemplate? NumberEditor { get; set; }

    public IDataTemplate? EnumEditor { get; set; }
    public IDataTemplate? FlagsEditor { get; set; }

    public IDataTemplate? BrushEditor { get; set; }

    public IDataTemplate? ColorEditor { get; set; }

    public IDataTemplate? ThicknessEditor { get; set; }

    public IDataTemplate? CornerRadiusEditor { get; set; }

    public IDataTemplate? PointEditor { get; set; }

    public IDataTemplate? SizeEditor { get; set; }

    public IDataTemplate? RectEditor { get; set; }

    public IDataTemplate? GridLengthEditor { get; set; }

    public IDataTemplate? FontFamilyEditor { get; set; }

    public IDataTemplate? FontWeightEditor { get; set; }

    public IDataTemplate? FontStyleEditor { get; set; }

    public IDataTemplate? TimeSpanEditor { get; set; }

    public IDataTemplate? UriEditor { get; set; }

    public IDataTemplate? CollectionEditor { get; set; }

    public IDataTemplate? TemplateEditor { get; set; }

    public IDataTemplate? MarkupExtensionEditor { get; set; }

    public bool Match(object? data)
    {
        return data is PropertyEntryViewModel;
    }

    public Control? Build(object? data)
    {
        if (data is not PropertyEntryViewModel entry)
        {
            return null;
        }

        IDataTemplate? template = entry.EditorKind switch
        {
            PropertyEditorKind.Boolean => BooleanEditor,
            PropertyEditorKind.Number => NumberEditor,
            PropertyEditorKind.Enum => EnumEditor,
            PropertyEditorKind.Brush => BrushEditor,
            PropertyEditorKind.Color => ColorEditor,
            PropertyEditorKind.Thickness => ThicknessEditor,
            PropertyEditorKind.CornerRadius => CornerRadiusEditor,
            PropertyEditorKind.Point => PointEditor,
            PropertyEditorKind.Size => SizeEditor,
            PropertyEditorKind.Rect => RectEditor,
            PropertyEditorKind.GridLength => GridLengthEditor,
            PropertyEditorKind.FontFamily => FontFamilyEditor,
            PropertyEditorKind.FontWeight => FontWeightEditor,
            PropertyEditorKind.FontStyle => FontStyleEditor,
            PropertyEditorKind.TimeSpan => TimeSpanEditor,
            PropertyEditorKind.Uri => UriEditor,
            PropertyEditorKind.Collection => CollectionEditor,
            PropertyEditorKind.Template => TemplateEditor,
            PropertyEditorKind.MarkupExtension => MarkupExtensionEditor,
            _ => TextEditor
        };

        if (entry.IsFlagsEnum) template = FlagsEditor;
        Control? typedEditor = (template ?? TextEditor)?.Build(data);
        if (typedEditor is null) return null;

        typedEditor.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        StackPanel field = new() { Spacing = 3, Margin = new Avalonia.Thickness(0, 2) };
        if (entry.EditorKind is not PropertyEditorKind.Collection and not PropertyEditorKind.Template)
        {
            TextBox expressionEditor = new() { IsReadOnly = entry.IsReadOnly, PlaceholderText = "Binding or resource expression" };
            expressionEditor.Bind(TextBox.TextProperty, new Binding(nameof(PropertyEntryViewModel.Value))
            {
                Source = entry, Mode = BindingMode.TwoWay
            });
            expressionEditor.Bind(Control.IsVisibleProperty, new Binding(nameof(PropertyEntryViewModel.IsExpression)) { Source = entry });
            typedEditor.Bind(Control.IsVisibleProperty, new Binding(nameof(PropertyEntryViewModel.IsExpression))
            {
                Source = entry, Converter = new BooleanInverseConverter()
            });
            field.Children.Add(expressionEditor);
        }
        if (entry.EditorKind == PropertyEditorKind.GridLength)
        {
            DockPanel gridLengthEditor = new();
            gridLengthEditor.Bind(Control.IsVisibleProperty, new Binding(nameof(PropertyEntryViewModel.IsExpression))
            {
                Source = entry, Converter = new BooleanInverseConverter()
            });
            ComboBox units = new()
            {
                ItemsSource = Enum.GetValues<GridUnitType>(),
                MinWidth = 80,
                Margin = new Avalonia.Thickness(6, 0, 0, 0),
                IsEnabled = !entry.IsReadOnly
            };
            units.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(PropertyEntryViewModel.Value))
            {
                Source = entry, Mode = BindingMode.TwoWay, Converter = new GridLengthUnitConverter(entry)
            });
            ToolTip.SetTip(units, "Auto size, fixed pixels, or proportional star (*) sizing");
            units.DropDownClosed += (_, _) =>
            {
                if (!entry.IsReadOnly && !entry.IsExpression) ((ICommand)entry.CommitCommand).Execute(null);
            };
            DockPanel.SetDock(units, Dock.Right);
            gridLengthEditor.Children.Add(units);
            gridLengthEditor.Children.Add(typedEditor);
            field.Children.Add(gridLengthEditor);
        }
        else
        {
            field.Children.Add(typedEditor);
        }
        TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed, FontSize = 11 };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(PropertyEntryViewModel.ValidationError)) { Source = entry });
        error.Bind(Control.IsVisibleProperty, new Binding(nameof(PropertyEntryViewModel.HasValidationError)) { Source = entry });
        field.Children.Add(error);
        field.AddHandler(InputElement.LostFocusEvent, (_, _) => ((ICommand)entry.CommitCommand).Execute(null), RoutingStrategies.Bubble);
        field.AddHandler(InputElement.KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                ((ICommand)entry.CancelEditCommand).Execute(null);
                args.Handled = true;
            }
            else if (args.Key == Key.Enter && args.Source is not TextBox { AcceptsReturn: true })
            {
                ((ICommand)entry.CommitCommand).Execute(null);
                args.Handled = true;
            }
        }, RoutingStrategies.Bubble);
        return field;
    }
}
