using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Folding;
using ReactiveUI;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class TextEditorFeaturesTests
{
    [AvaloniaFact]
    public async Task OptionsReachEveryEditorAndItsDocument()
    {
        using EditorFixture fixture = await EditorFixture.CreateAsync(2);

        fixture.Host.SetOptions(new TextEditorViewOptions
        {
            FontSize = 20,
            WordWrap = true,
            ShowColumnRuler = true,
            ColumnRulerPosition = 100,
            ShowTabs = true,
            ConvertTabsToSpaces = true
        });

        foreach ((TextEditor editor, DisplayState state) in fixture.Editors)
        {
            Assert.Equal(20, state.FontSize);
            Assert.True(state.WordWrap);
            Assert.True(editor.Options.ShowColumnRulers);
            Assert.Equal(new[] { 100 }, editor.Options.ColumnRulerPositions);
            Assert.True(editor.Options.ShowTabs);
            Assert.True(editor.Options.ConvertTabsToSpaces);
        }
    }

    [AvaloniaFact]
    public async Task DocumentToolbarChangesTheOptionOfAllEditors()
    {
        using EditorFixture fixture = await EditorFixture.CreateAsync(2);
        DisplayState first = fixture.Editors[0].State;
        DisplayState second = fixture.Editors[1].State;
        int changes = 0;
        fixture.Host.OptionsChanged += (_, _) => changes++;

        first.WordWrap = true;
        fixture.Host.SetOptions(fixture.Host.Options with { ShowColumnRuler = true });

        Assert.True(fixture.Host.Options.WordWrap);
        Assert.True(second.WordWrap);
        Assert.True(first.WordWrap);
        Assert.Equal(2, changes);
    }

    [AvaloniaFact]
    public async Task ColorOptionWinsOverTheThemeAndClearingItRestoresTheTheme()
    {
        using EditorFixture fixture = await EditorFixture.CreateAsync(1);
        TextEditor editor = fixture.Editors[0].Editor;
        IBrush theme = new SolidColorBrush(Colors.DarkSlateGray);
        editor.Background = theme;

        fixture.Host.SetOptions(new TextEditorViewOptions { BackgroundColor = "#FFFFE0" });
        Assert.Equal(Color.Parse("#FFFFE0"), Assert.IsAssignableFrom<ISolidColorBrush>(editor.Background).Color);

        editor.Background = new SolidColorBrush(Colors.Black);
        Assert.Equal(Color.Parse("#FFFFE0"), Assert.IsAssignableFrom<ISolidColorBrush>(editor.Background).Color);

        fixture.Reattach(editor);
        fixture.Host.SetOptions(new TextEditorViewOptions());

        Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(editor.Background).Color);
    }

    [AvaloniaFact]
    public async Task ContextMenuFollowsTheBehavior()
    {
        using EditorFixture fixture = await EditorFixture.CreateAsync(1);
        TextEditor editor = fixture.Editors[0].Editor;

        Assert.Same(fixture.Host.ContextMenuItems, editor.ContextMenu?.ItemsSource);
        Assert.Contains("editor-context", editor.ContextMenu!.Classes);

        TextEditorFeaturesBehavior.SetIsEnabled(editor, false);

        Assert.Null(editor.ContextMenu);
    }

    [AvaloniaFact]
    public async Task ViewActionsRunOnTheEditorOfTheActiveDocument()
    {
        using EditorFixture fixture = await EditorFixture.CreateAsync(2);
        TextEditor first = fixture.Editors[0].Editor;
        TextEditor second = fixture.Editors[1].Editor;

        fixture.ActiveDocument = null;
        Assert.False(fixture.Host.HasActiveEditor);
        Assert.True(fixture.ContextKeys.TryGet(TextEditorContextKeys.HasActiveEditor, out bool hasEditor) && !hasEditor);
        Assert.False(await fixture.Host.ExecuteAsync(TextEditorViewAction.Find, CancellationToken.None));

        fixture.ActiveDocument = fixture.Editors[1].State;
        Assert.True(fixture.ContextKeys.TryGet(TextEditorContextKeys.HasActiveEditor, out hasEditor) && hasEditor);
        first.TextArea.Focus();
        Assert.True(await fixture.Host.ExecuteAsync(TextEditorViewAction.Replace, CancellationToken.None));
        Assert.True(second.SearchPanel.IsOpened);
        Assert.True(second.SearchPanel.IsReplaceMode);
        Assert.False(first.SearchPanel.IsOpened);

        Assert.False(await fixture.Host.ExecuteAsync(TextEditorViewAction.CollapseAllFoldings, CancellationToken.None));
        FoldingManager foldings = FoldingManager.Install(second.TextArea);
        FoldingSection section = foldings.CreateFolding(2, 9);
        Assert.True(await fixture.Host.ExecuteAsync(TextEditorViewAction.CollapseAllFoldings, CancellationToken.None));
        Assert.True(section.IsFolded);
        Assert.True(await fixture.Host.ExecuteAsync(TextEditorViewAction.ExpandAllFoldings, CancellationToken.None));
        Assert.False(section.IsFolded);
        FoldingManager.Uninstall(foldings);

        TextEditorFeaturesBehavior.SetIsEnabled(second, false);
        Assert.True(fixture.ContextKeys.TryGet(TextEditorContextKeys.HasActiveEditor, out hasEditor) && !hasEditor);
    }

    private sealed class EditorFixture : IDisposable
    {
        private readonly IStyle[] _styles;
        private readonly BehaviorSubject<object?> _activeDocument = new(null);
        private readonly StackPanel _panel = new();
        private readonly Window _window;

        private EditorFixture(int editorCount)
        {
            Uri baseUri = new("avares://XamlVisualEditor.App/");
            _styles =
            [
                new Avalonia.Themes.Fluent.FluentTheme(),
                new StyleInclude(baseUri) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") },
                new StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorDensity.axaml") },
                new StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorContextMenu.axaml") }
            ];
            Host = new TextEditorViewHost(
                new ObservableCollection<ExtensionMenuItemViewModel>
                {
                    new("test.command", "Command", ExtensionMenuLocations.EditorContext, "1", 0, ReactiveCommand.Create(() => { }))
                },
                _activeDocument,
                ContextKeys);
            Application.Current!.Styles.AddRange(_styles);
            Application.Current.Resources[TextEditorViewHost.RESOURCE_KEY] = Host;
            for (int index = 0; index < editorCount; index++)
            {
                DisplayState state = new();
                TextEditor editor = new() { DataContext = state, Text = "a\n{\n  b\n}\n", Height = 100 };
                TextEditorFeaturesBehavior.SetIsEnabled(editor, true);
                _panel.Children.Add(editor);
                Editors.Add((editor, state));
            }

            ActiveDocument = Editors[0].State;
            _window = new Window { Content = _panel, Width = 500, Height = 300 };
        }

        public TextEditorViewHost Host { get; }

        public List<(TextEditor Editor, DisplayState State)> Editors { get; } = new();

        public ContextKeyRegistry ContextKeys { get; } = new();

        public object? ActiveDocument
        {
            get => _activeDocument.Value;
            set => _activeDocument.OnNext(value);
        }

        public static async Task<EditorFixture> CreateAsync(int editorCount)
        {
            EditorFixture fixture = new(editorCount);
            fixture._window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            return fixture;
        }

        public void Reattach(TextEditor editor)
        {
            int index = _panel.Children.IndexOf(editor);
            _panel.Children.RemoveAt(index);
            _panel.Children.Insert(index, editor);
        }

        public void Dispose()
        {
            _window.Close();
            Host.Dispose();
            Application.Current!.Resources.Remove(TextEditorViewHost.RESOURCE_KEY);
            foreach (IStyle style in _styles)
            {
                Application.Current.Styles.Remove(style);
            }
        }
    }

    private sealed class DisplayState : ITextEditorDisplayState
    {
        private double _fontSize = 14;
        private bool _wordWrap;
        private bool _showLineNumbers = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public double FontSize { get => _fontSize; set => Set(ref _fontSize, value); }

        public bool WordWrap { get => _wordWrap; set => Set(ref _wordWrap, value); }

        public bool ShowLineNumbers { get => _showLineNumbers; set => Set(ref _showLineNumbers, value); }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
