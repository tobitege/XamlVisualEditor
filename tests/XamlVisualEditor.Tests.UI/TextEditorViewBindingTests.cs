using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.CodeEditor;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Intellisense;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class TextEditorViewBindingTests
{
    [AvaloniaFact]
    public async Task TextFileViewKeepsTheCaretOfItsDocumentAndAppliesSelections()
    {
        using TextDocumentViewModel document = new(Path.Combine(Path.GetTempPath(), "binding.cs"));
        document.Document.Text = "first line\nsecond line\n";
        document.SetCaretOffset(14);

        await WithViewAsync(new TextFileView { DataContext = document }, async editor =>
        {
            Assert.Equal(14, editor.TextArea.Caret.Offset);
            Assert.Equal(14, document.CaretOffset);

            document.SetSelection(11, 6, 17);
            await Flush();
            Assert.Equal((11, 6, 17), (editor.SelectionStart, editor.SelectionLength, editor.TextArea.Caret.Offset));

            document.SetSelection(4, 0, 4);
            await Flush();
            Assert.Equal((0, 4), (editor.SelectionLength, editor.TextArea.Caret.Offset));
            Assert.Equal(4, document.CaretOffset);
        });
    }

    [AvaloniaFact]
    public async Task XamlCodeEditorViewReportsItsSelectionAndKeepsTheCaret()
    {
        using CodeEditorViewModel document = new(
            Path.Combine(Path.GetTempPath(), "binding.axaml"),
            new SyncEngine(new XamlParsingService(), new XamlSerializationService()),
            CompletionProviderRegistry.CreateDefault());
        document.Document.Text = "<Grid>\n  <Button />\n</Grid>\n";
        document.SetCaretOffset(9);

        await WithViewAsync(new XamlCodeEditorView { DataContext = document }, async editor =>
        {
            Assert.Equal(9, editor.TextArea.Caret.Offset);
            Assert.Equal(9, document.CaretOffset);

            editor.Select(9, 6);
            await Flush();
            Assert.Equal((9, 6), (document.SelectionStart, document.SelectionLength));
        });
    }

    private static async Task WithViewAsync(Control view, Func<TextEditor, Task> test)
    {
        Uri baseUri = new("avares://XamlVisualEditor.App/");
        IStyle[] styles =
        [
            new Avalonia.Themes.Fluent.FluentTheme(),
            new StyleInclude(baseUri) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") }
        ];
        Application.Current!.Styles.AddRange(styles);
        Window window = new() { Content = view, Width = 600, Height = 400 };
        try
        {
            window.Show();
            await Flush();
            TextEditor editor = view.GetLogicalDescendants().OfType<TextEditor>().Single();
            await test(editor);
        }
        finally
        {
            window.Close();
            foreach (IStyle style in styles)
            {
                Application.Current.Styles.Remove(style);
            }
        }
    }

    private static Task Flush()
    {
        return Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();
    }
}
