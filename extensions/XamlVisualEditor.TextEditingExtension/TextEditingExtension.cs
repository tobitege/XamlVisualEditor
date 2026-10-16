using Avalonia.Media;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TextEditingExtension.Localization;
using XamlVisualEditor.TextEditingExtension.Services;
using XamlVisualEditor.TextEditingExtension.ViewModels;
using XamlVisualEditor.TextEditingExtension.Views;

namespace XamlVisualEditor.TextEditingExtension;

/// <summary>Text editing commands, the editor context menu and the editor options dialog.</summary>
public sealed class TextEditingExtension : IXveExtension
{
    internal const string OPTIONS_DIALOG_ID = "textEditing.options.dialog";
    internal const string FIND_COMMAND_ID = "textEditing.find";
    internal const string REPLACE_COMMAND_ID = "textEditing.replace";
    internal const string MATCHING_BRACKET_COMMAND_ID = "textEditing.goToMatchingBracket";
    internal const string TOGGLE_COMMENT_COMMAND_ID = "textEditing.toggleComment";
    internal const string COMMENT_COMMAND_ID = "textEditing.comment";
    internal const string UNCOMMENT_COMMAND_ID = "textEditing.uncomment";
    internal const string UPPERCASE_COMMAND_ID = "textEditing.upperCase";
    internal const string LOWERCASE_COMMAND_ID = "textEditing.lowerCase";
    internal const string ENCLOSE_COMMAND_ID = "textEditing.encloseInBrackets";
    internal const string INDENT_COMMAND_ID = "textEditing.indent";
    internal const string UNINDENT_COMMAND_ID = "textEditing.unindent";
    internal const string SORT_ASCENDING_COMMAND_ID = "textEditing.sortAscending";
    internal const string SORT_DESCENDING_COMMAND_ID = "textEditing.sortDescending";
    internal const string EXPAND_ALL_COMMAND_ID = "textEditing.outlining.expandAll";
    internal const string COLLAPSE_ALL_COMMAND_ID = "textEditing.outlining.collapseAll";
    internal const string COLLAPSE_SELECTION_COMMAND_ID = "textEditing.outlining.collapseSelection";
    internal const string ZOOM_IN_COMMAND_ID = "textEditing.zoomIn";
    internal const string ZOOM_OUT_COMMAND_ID = "textEditing.zoomOut";
    internal const string RESET_ZOOM_COMMAND_ID = "textEditing.resetZoom";
    internal const string WORD_WRAP_COMMAND_ID = "textEditing.toggle.wordWrap";
    internal const string LINE_NUMBERS_COMMAND_ID = "textEditing.toggle.lineNumbers";
    internal const string FOLDING_MARGIN_COMMAND_ID = "textEditing.toggle.foldingMargin";
    internal const string COLUMN_RULER_COMMAND_ID = "textEditing.toggle.columnRuler";
    internal const string CURRENT_LINE_COMMAND_ID = "textEditing.toggle.currentLine";
    internal const string WHITESPACE_COMMAND_ID = "textEditing.toggle.whitespace";
    internal const string END_OF_LINE_COMMAND_ID = "textEditing.toggle.endOfLine";
    internal const string ALL_CHARACTERS_COMMAND_ID = "textEditing.toggle.allCharacters";
    internal const string OPTIONS_COMMAND_ID = "textEditing.options";

    // Context keys that carry the check marks of the display toggles.
    internal const string WORD_WRAP_KEY = "textEditing.wordWrap";
    internal const string LINE_NUMBERS_KEY = "textEditing.lineNumbers";
    internal const string FOLDING_MARGIN_KEY = "textEditing.foldingMargin";
    internal const string COLUMN_RULER_KEY = "textEditing.columnRuler";
    internal const string CURRENT_LINE_KEY = "textEditing.currentLine";
    internal const string WHITESPACE_KEY = "textEditing.whitespace";
    internal const string END_OF_LINE_KEY = "textEditing.endOfLine";
    internal const string ALL_CHARACTERS_KEY = "textEditing.allCharacters";

    // Commands of other extensions that the context menu offers.
    private const string UNDO_COMMAND_ID = "editing.undo";
    private const string REDO_COMMAND_ID = "editing.redo";
    private const string CUT_COMMAND_ID = "editing.cut";
    private const string COPY_COMMAND_ID = "editing.copy";
    private const string PASTE_COMMAND_ID = "editing.paste";
    private const string SELECT_ALL_COMMAND_ID = "editing.selectAll";
    private const string FORMAT_DOCUMENT_COMMAND_ID = "navigation.formatDocument";
    private const string GO_TO_LINE_COMMAND_ID = "navigation.goToLine";

    private const string WHEN_DOCUMENT = "hasActiveDocument";
    private const string WHEN_EDITOR = TextEditorContextKeys.HasActiveEditor;
    private const double ZOOM_STEP = 1d;

    /// <inheritdoc />
    public async Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        RegisterDocumentCommands(context);
        if (context.TextEditorView is { } view)
        {
            TextEditorOptionsStore store = new(context.Storage);
            if (await store.LoadAsync(cancellationToken).ConfigureAwait(true) is { } saved)
            {
                view.SetOptions(saved);
            }

            OptionsPersistence persistence = new(view, store, context.ContextKeys, context.Logger);
            context.Subscriptions.Add(persistence);
            RegisterViewCommands(context, view, persistence);
        }

        context.Subscriptions.Add(context.Contributions.RegisterMenuItems(context.ExtensionId,
            MenuLayout.Build(context.TextEditorView is not null)));
        context.Subscriptions.Add(context.Contributions.RegisterCommandPaletteItems(context.ExtensionId,
            MenuLayout.BuildPalette(context.TextEditorView is not null)));

        CaretPositionMemory caretPositions = new(context.Editor, context.Storage, context.Logger);
        context.Subscriptions.Add(caretPositions);
        await caretPositions.LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    private static void RegisterDocumentCommands(ExtensionContext context)
    {
        RegisterEdit(context, TOGGLE_COMMENT_COMMAND_ID, TextEditingStrings.ToggleComment, null,
            (text, selection, syntax) => syntax is null ? null : TextEditOperations.ToggleComment(text, selection, syntax, GetTabSize(context)));
        RegisterEdit(context, COMMENT_COMMAND_ID, TextEditingStrings.CommentSelection, null,
            (text, selection, syntax) => syntax is null ? null : TextEditOperations.Comment(text, selection, syntax, GetTabSize(context)));
        RegisterEdit(context, UNCOMMENT_COMMAND_ID, TextEditingStrings.UncommentSelection, null,
            (text, selection, syntax) => syntax is null ? null : TextEditOperations.Uncomment(text, selection, syntax));
        RegisterEdit(context, UPPERCASE_COMMAND_ID, TextEditingStrings.MakeUppercase, "Ctrl+Shift+U",
            (text, selection, _) => TextEditOperations.ToUpperCase(text, selection));
        RegisterEdit(context, LOWERCASE_COMMAND_ID, TextEditingStrings.MakeLowercase, "Ctrl+U",
            (text, selection, _) => TextEditOperations.ToLowerCase(text, selection));
        RegisterEdit(context, ENCLOSE_COMMAND_ID, TextEditingStrings.EncloseInBrackets, null,
            (text, selection, _) => TextEditOperations.WrapInBrackets(text, selection));
        RegisterEdit(context, INDENT_COMMAND_ID, TextEditingStrings.IncreaseIndent, null,
            (text, selection, _) => TextEditOperations.Indent(text, selection, GetIndentUnit(context.TextEditorView?.Options)));
        RegisterEdit(context, UNINDENT_COMMAND_ID, TextEditingStrings.DecreaseIndent, null,
            (text, selection, _) => TextEditOperations.Unindent(text, selection,
                (context.TextEditorView?.Options ?? new TextEditorViewOptions()).IndentationSize));
        RegisterEdit(context, SORT_ASCENDING_COMMAND_ID, TextEditingStrings.SortLinesAscending, null,
            (text, selection, _) => TextEditOperations.SortLines(text, selection, ascending: true));
        RegisterEdit(context, SORT_DESCENDING_COMMAND_ID, TextEditingStrings.SortLinesDescending, null,
            (text, selection, _) => TextEditOperations.SortLines(text, selection, ascending: false));
        RegisterEdit(context, MATCHING_BRACKET_COMMAND_ID, TextEditingStrings.GoToMatchingBracket, null,
            (text, selection, syntax) => TextEditOperations.FindMatchingBracket(text, selection.CaretOffset, syntax) is { } match
                ? new TextEditResult(Array.Empty<Core.TextEdit>(), match)
                : null);
    }

    private static void RegisterViewCommands(ExtensionContext context, ITextEditorViewHost view, OptionsPersistence persistence)
    {
        RegisterViewAction(context, view, FIND_COMMAND_ID, TextEditingStrings.Find, "Ctrl+F", TextEditorViewAction.Find);
        RegisterViewAction(context, view, REPLACE_COMMAND_ID, TextEditingStrings.Replace, "Ctrl+H", TextEditorViewAction.Replace);
        RegisterViewAction(context, view, EXPAND_ALL_COMMAND_ID, TextEditingStrings.ExpandAll, null, TextEditorViewAction.ExpandAllFoldings);
        RegisterViewAction(context, view, COLLAPSE_ALL_COMMAND_ID, TextEditingStrings.CollapseAll, null, TextEditorViewAction.CollapseAllFoldings);
        RegisterViewAction(context, view, COLLAPSE_SELECTION_COMMAND_ID, TextEditingStrings.CollapseSelection, null, TextEditorViewAction.CollapseSelection);

        RegisterOptionChange(context, view, ZOOM_IN_COMMAND_ID, TextEditingStrings.ZoomIn,
            options => options with { FontSize = options.FontSize + ZOOM_STEP });
        RegisterOptionChange(context, view, ZOOM_OUT_COMMAND_ID, TextEditingStrings.ZoomOut,
            options => options with { FontSize = options.FontSize - ZOOM_STEP });
        RegisterOptionChange(context, view, RESET_ZOOM_COMMAND_ID, TextEditingStrings.ResetZoom,
            options => options with { FontSize = new TextEditorViewOptions().FontSize });
        RegisterOptionChange(context, view, WORD_WRAP_COMMAND_ID, TextEditingStrings.WordWrap,
            options => options with { WordWrap = !options.WordWrap });
        RegisterOptionChange(context, view, LINE_NUMBERS_COMMAND_ID, TextEditingStrings.LineNumbers,
            options => options with { ShowLineNumbers = !options.ShowLineNumbers });
        RegisterOptionChange(context, view, FOLDING_MARGIN_COMMAND_ID, TextEditingStrings.FoldingMargin,
            options => options with { ShowFoldingMargin = !options.ShowFoldingMargin });
        RegisterOptionChange(context, view, COLUMN_RULER_COMMAND_ID, TextEditingStrings.ColumnRuler,
            options => options with { ShowColumnRuler = !options.ShowColumnRuler });
        RegisterOptionChange(context, view, CURRENT_LINE_COMMAND_ID, TextEditingStrings.HighlightCurrentLine,
            options => options with { HighlightCurrentLine = !options.HighlightCurrentLine });
        RegisterOptionChange(context, view, WHITESPACE_COMMAND_ID, TextEditingStrings.ShowWhitespace,
            options =>
            {
                bool show = !OptionsPersistence.ShowsWhitespace(options);
                return options with { ShowSpaces = show, ShowTabs = show };
            });
        RegisterOptionChange(context, view, END_OF_LINE_COMMAND_ID, TextEditingStrings.ShowEndOfLine,
            options => options with { ShowEndOfLine = !options.ShowEndOfLine });
        RegisterOptionChange(context, view, ALL_CHARACTERS_COMMAND_ID, TextEditingStrings.ShowAllCharacters,
            options =>
            {
                bool show = !OptionsPersistence.ShowsAllCharacters(options);
                return options with { ShowSpaces = show, ShowTabs = show, ShowEndOfLine = show };
            });

        context.Subscriptions.Add(context.DialogHost.RegisterDialog(OPTIONS_DIALOG_ID,
            input => new TextEditorOptionsDialog { DataContext = input }));
        context.Subscriptions.Add(context.Commands.Register(OPTIONS_COMMAND_ID, async _ =>
        {
            TextEditorViewOptions original = view.Options;
            List<string> fonts = FontManager.Current.SystemFonts.Select(family => family.Name).ToList();
            using TextEditorOptionsViewModel dialog = new(original, fonts, view.SetOptions);
            using IDisposable preview = persistence.Pause();
            TextEditorViewOptions chosen = original;
            try
            {
                bool? saved = await context.DialogHost.ShowDialogAsync<bool?>(OPTIONS_DIALOG_ID, dialog, CancellationToken.None);
                if (saved == true && dialog.Result is { } result)
                {
                    chosen = result;
                }
            }
            finally
            {
                // Runs before the pause ends, so that a failed dialog never saves its preview.
                view.SetOptions(chosen);
            }
        }));
        RegisterMetadata(context, OPTIONS_COMMAND_ID, TextEditingStrings.EditorOptions, null, when: null);
    }

    private static void RegisterEdit(
        ExtensionContext context,
        string commandId,
        string title,
        string? keybinding,
        Func<string, TextSelection, CommentSyntax?, TextEditResult?> operation)
    {
        context.Subscriptions.Add(context.Commands.Register(commandId, async _ =>
        {
            if (context.Editor.ActiveDocument is not { } document)
            {
                return;
            }

            string text = await document.GetTextAsync(CancellationToken.None);
            TextSelection selection = new(document.SelectionStart, document.SelectionLength, document.CaretOffset);
            CommentSyntax? syntax = CommentSyntax.Resolve(document.FilePath, document.LanguageId);
            if (operation(text, selection, syntax) is not { } result)
            {
                return;
            }

            if (result.Edits.Count > 0)
            {
                await document.ApplyEditsAsync(result.Edits, CancellationToken.None);
            }

            document.SetSelection(result.Selection.Start, result.Selection.Length, result.Selection.CaretOffset);
        }));
        RegisterMetadata(context, commandId, title, keybinding, WHEN_DOCUMENT);
    }

    private static void RegisterViewAction(
        ExtensionContext context,
        ITextEditorViewHost view,
        string commandId,
        string title,
        string? keybinding,
        TextEditorViewAction action)
    {
        context.Subscriptions.Add(context.Commands.Register(commandId,
            _ => view.ExecuteAsync(action, CancellationToken.None)));
        RegisterMetadata(context, commandId, title, keybinding, WHEN_EDITOR);
    }

    // The persistence saves the changed options.
    private static void RegisterOptionChange(
        ExtensionContext context,
        ITextEditorViewHost view,
        string commandId,
        string title,
        Func<TextEditorViewOptions, TextEditorViewOptions> change)
    {
        context.Subscriptions.Add(context.Commands.Register(commandId, _ =>
        {
            view.SetOptions(change(view.Options));
            return Task.CompletedTask;
        }));
        RegisterMetadata(context, commandId, title, null, when: null);
    }

    // The keybindings act on the text under the caret, so they apply only while an editor has the focus.
    private static void RegisterMetadata(ExtensionContext context, string commandId, string title, string? keybinding, string? when)
    {
        context.Subscriptions.Add(context.CommandMetadata.Register(commandId, new CommandMetadata(
            Title: title.TrimEnd('.'),
            Category: TextEditingStrings.Category,
            When: when,
            Keybinding: keybinding)
        {
            KeybindingScope = CommandKeybindingScope.TextEditor
        }));
    }

    // The editor draws a tab as wide as one indentation level.
    private static int GetTabSize(ExtensionContext context)
    {
        return context.TextEditorView?.Options.IndentationSize ?? TextEditOperations.DEFAULT_TAB_SIZE;
    }

    private static string GetIndentUnit(TextEditorViewOptions? options)
    {
        TextEditorViewOptions current = options ?? new TextEditorViewOptions();
        return current.ConvertTabsToSpaces ? new string(' ', current.IndentationSize) : "\t";
    }

    /// <summary>
    /// Saves every change of the options and publishes the display toggles as context keys. While the options dialog
    /// previews its changes, saving waits for the dialog result. Saves run one after another away from the UI thread.
    /// </summary>
    internal sealed class OptionsPersistence : IDisposable
    {
        private static readonly TimeSpan ShutdownSaveTimeout = TimeSpan.FromSeconds(2);

        private readonly ITextEditorViewHost _view;
        private readonly TextEditorOptionsStore _store;
        private readonly IContextKeys? _contextKeys;
        private readonly IExtensionLogger _logger;
        private readonly object _gate = new();
        private TextEditorViewOptions? _requested;
        private Task _saving = Task.CompletedTask;
        private int _pauses;

        public OptionsPersistence(ITextEditorViewHost view, TextEditorOptionsStore store, IContextKeys? contextKeys, IExtensionLogger logger)
        {
            _view = view;
            _store = store;
            _contextKeys = contextKeys;
            _logger = logger;
            _requested = view.Options;
            _view.OptionsChanged += OnOptionsChanged;
            PublishContextKeys(view.Options);
        }

        /// <summary>Gets the save that runs last.</summary>
        public Task Saving
        {
            get
            {
                lock (_gate)
                {
                    return _saving;
                }
            }
        }

        public static bool ShowsWhitespace(TextEditorViewOptions options) => options.ShowSpaces && options.ShowTabs;

        public static bool ShowsAllCharacters(TextEditorViewOptions options) => ShowsWhitespace(options) && options.ShowEndOfLine;

        /// <summary>Stops saving until the returned handle is disposed; then the options at that time are saved.</summary>
        public IDisposable Pause()
        {
            _pauses++;
            return System.Reactive.Disposables.Disposable.Create(() =>
            {
                _pauses--;
                Save(_view.Options);
            });
        }

        /// <summary>Stops following the options; shutdown waits a short time for the running save.</summary>
        public void Dispose()
        {
            _view.OptionsChanged -= OnOptionsChanged;
            if (!Saving.Wait(ShutdownSaveTimeout))
            {
                _logger.Warn("The editor options were not saved before shutdown.");
            }
        }

        private void OnOptionsChanged(object? sender, EventArgs e)
        {
            TextEditorViewOptions options = _view.Options;
            PublishContextKeys(options);
            if (_pauses == 0)
            {
                Save(options);
            }
        }

        private void PublishContextKeys(TextEditorViewOptions options)
        {
            _contextKeys?.Set(
            [
                new(WORD_WRAP_KEY, options.WordWrap),
                new(LINE_NUMBERS_KEY, options.ShowLineNumbers),
                new(FOLDING_MARGIN_KEY, options.ShowFoldingMargin),
                new(COLUMN_RULER_KEY, options.ShowColumnRuler),
                new(CURRENT_LINE_KEY, options.HighlightCurrentLine),
                new(WHITESPACE_KEY, ShowsWhitespace(options)),
                new(END_OF_LINE_KEY, options.ShowEndOfLine),
                new(ALL_CHARACTERS_KEY, ShowsAllCharacters(options))
            ]);
        }

        // A failed save forgets the request, so the same options are written again on the next change.
        private void Save(TextEditorViewOptions options)
        {
            lock (_gate)
            {
                if (options == _requested)
                {
                    return;
                }

                _requested = options;
                Task previous = _saving;
                _saving = Task.Run(async () =>
                {
                    await previous.ConfigureAwait(false);
                    try
                    {
                        await _store.SaveAsync(options, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        lock (_gate)
                        {
                            if (_requested == options)
                            {
                                _requested = null;
                            }
                        }

                        _logger.Error("Saving the editor options failed.", error);
                    }
                });
            }
        }
    }

    /// <summary>Places the commands in the editor context menu, the edit menu and the command palette.</summary>
    internal static class MenuLayout
    {
        private const string EDIT_GROUP = "1.edit";
        private const string CLIPBOARD_GROUP = "2.clipboard";
        private const string FIND_GROUP = "3.find";
        private const string FORMAT_GROUP = "4.format";
        private const string OUTLINING_GROUP = "5.outlining";
        private const string DISPLAY_GROUP = "6.display";
        private const string OPTIONS_GROUP = "9.options";
        private const string EDIT_MENU_ADVANCED_GROUP = "4.advanced";

        public static List<ExtensionMenuContribution> Build(bool hasView)
        {
            string context = ExtensionMenuLocations.EditorContext;
            string format = TextEditingStrings.FormatMenu;
            List<ExtensionMenuContribution> items =
            [
                new(UNDO_COMMAND_ID, UiStrings.Undo, context, EDIT_GROUP, 0),
                new(REDO_COMMAND_ID, UiStrings.Redo, context, EDIT_GROUP, 10),
                new(CUT_COMMAND_ID, UiStrings.Cut, context, CLIPBOARD_GROUP, 0),
                new(COPY_COMMAND_ID, UiStrings.Copy, context, CLIPBOARD_GROUP, 10),
                new(PASTE_COMMAND_ID, UiStrings.Paste, context, CLIPBOARD_GROUP, 20),
                new(SELECT_ALL_COMMAND_ID, UiStrings.SelectAll, context, CLIPBOARD_GROUP, 30),
                new(GO_TO_LINE_COMMAND_ID, UiStrings.GoToLineDialog, context, FIND_GROUP, 20),
                new(MATCHING_BRACKET_COMMAND_ID, TextEditingStrings.GoToMatchingBracket, context, FIND_GROUP, 30),
                new(FORMAT_DOCUMENT_COMMAND_ID, UiStrings.FormatDocument, context, FORMAT_GROUP, 0)
                {
                    Submenu = format,
                    SubmenuGroup = "0.document"
                }
            ];
            items.AddRange(BuildFormatItems(context, FORMAT_GROUP, format));
            items.AddRange(BuildFormatItems(ExtensionMenuLocations.Edit, EDIT_MENU_ADVANCED_GROUP, TextEditingStrings.AdvancedMenu));
            if (!hasView)
            {
                return items;
            }

            string outlining = TextEditingStrings.OutliningMenu;
            string display = TextEditingStrings.DisplayMenu;
            items.AddRange(
            [
                new(FIND_COMMAND_ID, TextEditingStrings.Find, context, FIND_GROUP, 0),
                new(REPLACE_COMMAND_ID, TextEditingStrings.Replace, context, FIND_GROUP, 10),
                new(EXPAND_ALL_COMMAND_ID, TextEditingStrings.ExpandAll, context, OUTLINING_GROUP, 0) { Submenu = outlining },
                new(COLLAPSE_ALL_COMMAND_ID, TextEditingStrings.CollapseAll, context, OUTLINING_GROUP, 10) { Submenu = outlining },
                new(COLLAPSE_SELECTION_COMMAND_ID, TextEditingStrings.CollapseSelection, context, OUTLINING_GROUP, 20) { Submenu = outlining },
                Toggle(WORD_WRAP_COMMAND_ID, TextEditingStrings.WordWrap, "1.view", 0, WORD_WRAP_KEY, display),
                Toggle(LINE_NUMBERS_COMMAND_ID, TextEditingStrings.LineNumbers, "1.view", 10, LINE_NUMBERS_KEY, display),
                Toggle(FOLDING_MARGIN_COMMAND_ID, TextEditingStrings.FoldingMargin, "1.view", 20, FOLDING_MARGIN_KEY, display),
                Toggle(COLUMN_RULER_COMMAND_ID, TextEditingStrings.ColumnRuler, "1.view", 30, COLUMN_RULER_KEY, display),
                Toggle(CURRENT_LINE_COMMAND_ID, TextEditingStrings.HighlightCurrentLine, "1.view", 40, CURRENT_LINE_KEY, display),
                Toggle(WHITESPACE_COMMAND_ID, TextEditingStrings.ShowWhitespace, "2.characters", 0, WHITESPACE_KEY, display),
                Toggle(END_OF_LINE_COMMAND_ID, TextEditingStrings.ShowEndOfLine, "2.characters", 10, END_OF_LINE_KEY, display),
                Toggle(ALL_CHARACTERS_COMMAND_ID, TextEditingStrings.ShowAllCharacters, "2.characters", 20, ALL_CHARACTERS_KEY, display),
                new(ZOOM_IN_COMMAND_ID, TextEditingStrings.ZoomIn, context, DISPLAY_GROUP, 100) { Submenu = display, SubmenuGroup = "3.zoom" },
                new(ZOOM_OUT_COMMAND_ID, TextEditingStrings.ZoomOut, context, DISPLAY_GROUP, 110) { Submenu = display, SubmenuGroup = "3.zoom" },
                new(RESET_ZOOM_COMMAND_ID, TextEditingStrings.ResetZoom, context, DISPLAY_GROUP, 120) { Submenu = display, SubmenuGroup = "3.zoom" },
                new(OPTIONS_COMMAND_ID, TextEditingStrings.EditorOptions, context, OPTIONS_GROUP, 0),
                new(FIND_COMMAND_ID, TextEditingStrings.Find, ExtensionMenuLocations.Edit, FIND_GROUP, 0),
                new(REPLACE_COMMAND_ID, TextEditingStrings.Replace, ExtensionMenuLocations.Edit, FIND_GROUP, 10),
                new(OPTIONS_COMMAND_ID, TextEditingStrings.EditorOptions, ExtensionMenuLocations.Edit, OPTIONS_GROUP, 0)
            ]);
            return items;

            static ExtensionMenuContribution Toggle(string commandId, string title, string subgroup, int priority, string key, string submenu)
            {
                return new ExtensionMenuContribution(commandId, title, ExtensionMenuLocations.EditorContext, DISPLAY_GROUP, priority)
                {
                    Submenu = submenu,
                    SubmenuGroup = subgroup,
                    CheckedWhen = key
                };
            }
        }

        public static List<ExtensionCommandPaletteContribution> BuildPalette(bool hasView)
        {
            List<(string Id, string Title)> commands =
            [
                (TOGGLE_COMMENT_COMMAND_ID, TextEditingStrings.ToggleComment),
                (COMMENT_COMMAND_ID, TextEditingStrings.CommentSelection),
                (UNCOMMENT_COMMAND_ID, TextEditingStrings.UncommentSelection),
                (UPPERCASE_COMMAND_ID, TextEditingStrings.MakeUppercase),
                (LOWERCASE_COMMAND_ID, TextEditingStrings.MakeLowercase),
                (ENCLOSE_COMMAND_ID, TextEditingStrings.EncloseInBrackets),
                (INDENT_COMMAND_ID, TextEditingStrings.IncreaseIndent),
                (UNINDENT_COMMAND_ID, TextEditingStrings.DecreaseIndent),
                (SORT_ASCENDING_COMMAND_ID, TextEditingStrings.SortLinesAscending),
                (SORT_DESCENDING_COMMAND_ID, TextEditingStrings.SortLinesDescending),
                (MATCHING_BRACKET_COMMAND_ID, TextEditingStrings.GoToMatchingBracket)
            ];
            if (hasView)
            {
                commands.AddRange(
                [
                    (FIND_COMMAND_ID, TextEditingStrings.Find),
                    (REPLACE_COMMAND_ID, TextEditingStrings.Replace),
                    (EXPAND_ALL_COMMAND_ID, TextEditingStrings.ExpandAll),
                    (COLLAPSE_ALL_COMMAND_ID, TextEditingStrings.CollapseAll),
                    (COLLAPSE_SELECTION_COMMAND_ID, TextEditingStrings.CollapseSelection),
                    (ZOOM_IN_COMMAND_ID, TextEditingStrings.ZoomIn),
                    (ZOOM_OUT_COMMAND_ID, TextEditingStrings.ZoomOut),
                    (RESET_ZOOM_COMMAND_ID, TextEditingStrings.ResetZoom),
                    (WORD_WRAP_COMMAND_ID, TextEditingStrings.WordWrap),
                    (LINE_NUMBERS_COMMAND_ID, TextEditingStrings.LineNumbers),
                    (FOLDING_MARGIN_COMMAND_ID, TextEditingStrings.FoldingMargin),
                    (COLUMN_RULER_COMMAND_ID, TextEditingStrings.ColumnRuler),
                    (CURRENT_LINE_COMMAND_ID, TextEditingStrings.HighlightCurrentLine),
                    (WHITESPACE_COMMAND_ID, TextEditingStrings.ShowWhitespace),
                    (END_OF_LINE_COMMAND_ID, TextEditingStrings.ShowEndOfLine),
                    (ALL_CHARACTERS_COMMAND_ID, TextEditingStrings.ShowAllCharacters),
                    (OPTIONS_COMMAND_ID, TextEditingStrings.EditorOptions)
                ]);
            }

            return commands
                .Select(command => new ExtensionCommandPaletteContribution(command.Id, command.Title.TrimEnd('.'), TextEditingStrings.Category))
                .ToList();
        }

        private static IEnumerable<ExtensionMenuContribution> BuildFormatItems(string location, string group, string submenu)
        {
            return
            [
                Item(TOGGLE_COMMENT_COMMAND_ID, TextEditingStrings.ToggleComment, "1.comment", 10),
                Item(COMMENT_COMMAND_ID, TextEditingStrings.CommentSelection, "1.comment", 20),
                Item(UNCOMMENT_COMMAND_ID, TextEditingStrings.UncommentSelection, "1.comment", 30),
                Item(UPPERCASE_COMMAND_ID, TextEditingStrings.MakeUppercase, "2.case", 40),
                Item(LOWERCASE_COMMAND_ID, TextEditingStrings.MakeLowercase, "2.case", 50),
                Item(INDENT_COMMAND_ID, TextEditingStrings.IncreaseIndent, "3.lines", 60),
                Item(UNINDENT_COMMAND_ID, TextEditingStrings.DecreaseIndent, "3.lines", 70),
                Item(SORT_ASCENDING_COMMAND_ID, TextEditingStrings.SortLinesAscending, "3.lines", 80),
                Item(SORT_DESCENDING_COMMAND_ID, TextEditingStrings.SortLinesDescending, "3.lines", 90),
                Item(ENCLOSE_COMMAND_ID, TextEditingStrings.EncloseInBrackets, "4.enclose", 100)
            ];

            ExtensionMenuContribution Item(string commandId, string title, string subgroup, int priority)
            {
                return new ExtensionMenuContribution(commandId, title, location, group, priority)
                {
                    Submenu = submenu,
                    SubmenuGroup = subgroup
                };
            }
        }
    }
}
