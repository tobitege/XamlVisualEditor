using System.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Intellisense;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;

namespace XamlVisualEditor.Xaml.Language;

/// <summary>
/// XAML language service built on the existing intellisense providers and parser.
/// </summary>
public sealed class XamlLanguageService : ILanguageIntellisenseService
{
    private readonly CompletionProviderRegistry _completionRegistry;
    private readonly IXamlParsingService _parser;
    private readonly ITypeMetadataService _metadataService;
    private readonly IDesignerCodeBehindRefactoringService? _codeBehindRefactoring;
    private readonly Func<IReadOnlyDictionary<string, string>>? _openDocumentTexts;
    private readonly XamlSerializationService _serializer = new();

    /// <summary>Creates the service without C# refactoring.</summary>
    public XamlLanguageService(
        CompletionProviderRegistry completionRegistry,
        IXamlParsingService parser,
        ITypeMetadataService metadataService)
        : this(completionRegistry, parser, metadataService, null, null)
    {
    }

    /// <summary>
    /// Creates the service. Without <paramref name="codeBehindRefactoring"/> names and event handlers of a document
    /// with code-behind are not renamed, because their C# declarations and references would stay behind.
    /// <paramref name="openDocumentTexts"/> returns the unsaved text of open C# documents by file path.
    /// </summary>
    public XamlLanguageService(
        CompletionProviderRegistry completionRegistry,
        IXamlParsingService parser,
        ITypeMetadataService metadataService,
        IDesignerCodeBehindRefactoringService? codeBehindRefactoring,
        Func<IReadOnlyDictionary<string, string>>? openDocumentTexts)
    {
        _completionRegistry = completionRegistry;
        _parser = parser;
        _metadataService = metadataService;
        _codeBehindRefactoring = codeBehindRefactoring;
        _openDocumentTexts = openDocumentTexts;
    }

    public string LanguageId => "xml";

    public bool CanHandle(string filePath, string? languageId)
    {
        if (string.Equals(languageId, "xml", StringComparison.OrdinalIgnoreCase))
        {
            return filePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                   filePath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);
        }

        return filePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
               filePath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);
    }

    public Task InitializeWorkspaceAsync(string workspacePath, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task ClearWorkspaceAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(
        CompletionContext context,
        CancellationToken ct = default)
    {
        CompletionContext enriched = new()
        {
            Offset = context.Offset,
            TextBefore = context.TextBefore,
            DocumentText = context.DocumentText,
            FilePath = context.FilePath,
            LanguageId = context.LanguageId,
            Trigger = context.Trigger,
            TriggerCharacter = context.TriggerCharacter,
            Metadata = _metadataService
        };

        IReadOnlyList<CompletionItem> items = _completionRegistry.GetCompletions(enriched);
        return Task.FromResult(items);
    }

    public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(
        LanguageDocumentContext context,
        CancellationToken ct = default)
    {
        ParseResult result = _parser.Parse(context.Text, new XamlParserOptions { UseTolerantParser = true });
        List<LanguageDiagnostic> diagnostics = new(result.Diagnostics.Count);

        foreach (XamlDiagnostic diagnostic in result.Diagnostics)
        {
            LanguageTextPosition start = new(diagnostic.Line, diagnostic.Column);
            LanguageTextPosition end = new(diagnostic.Line, diagnostic.Column + Math.Max(1, diagnostic.Length));

            diagnostics.Add(new LanguageDiagnostic
            {
                FilePath = context.FilePath,
                Message = diagnostic.Message,
                Severity = diagnostic.Severity,
                Range = new LanguageTextRange(start, end),
                Source = "XAML"
            });
        }

        return Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(diagnostics);
    }

    public Task<IReadOnlyList<LanguageSemanticToken>> GetSemanticTokensAsync(
        LanguageDocumentContext context,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context.Text))
        {
            return Task.FromResult<IReadOnlyList<LanguageSemanticToken>>(Array.Empty<LanguageSemanticToken>());
        }

        return Task.FromResult(BuildSemanticTokens(context.Text));
    }

    public Task<IReadOnlyList<TextEdit>> GetFormattingEditsAsync(
        LanguageDocumentContext context,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<TextEdit>>(Array.Empty<TextEdit>());
    }

    public Task<LanguageHover?> GetHoverAsync(LanguagePositionContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context.Text))
        {
            return Task.FromResult<LanguageHover?>(null);
        }

        string token = ExtractTokenAt(context.Text, context.Offset);
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult<LanguageHover?>(null);
        }

        TypeMetadata? type = _metadataService.GetType("https://github.com/avaloniaui", token);
        if (type is not null)
        {
            return Task.FromResult<LanguageHover?>(new LanguageHover
            {
                Contents = type.FullName
            });
        }

        return Task.FromResult<LanguageHover?>(null);
    }

    public Task<IReadOnlyList<LanguageLocation>> FindDefinitionsAsync(
        LanguagePositionContext context,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LanguageLocation>>(Array.Empty<LanguageLocation>());
    }

    public Task<IReadOnlyList<LanguageLocation>> FindReferencesAsync(
        LanguagePositionContext context,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LanguageLocation>>(Array.Empty<LanguageLocation>());
    }

    public Task<LanguageRenameInfo?> PrepareRenameAsync(
        LanguagePositionContext context,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context.Text))
        {
            return Task.FromResult<LanguageRenameInfo?>(null);
        }

        if (!TryGetRenameTarget(context.Text, context.Offset, out RenameTarget target) ||
            target.Kind != RenameTargetKind.ResourceKey && FindRenamedElement(context.Text, target) is null)
        {
            return Task.FromResult<LanguageRenameInfo?>(null);
        }

        LanguageTextPosition start = OffsetToPosition(context.Text, target.ValueStart);
        LanguageTextPosition end = OffsetToPosition(context.Text, target.ValueEnd);
        return Task.FromResult<LanguageRenameInfo?>(new LanguageRenameInfo
        {
            Name = target.Value,
            Range = new LanguageTextRange(start, end)
        });
    }

    public async Task<LanguageWorkspaceEdit?> RenameSymbolAsync(
        LanguageRenameContext context,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context.Text) || string.IsNullOrWhiteSpace(context.NewName))
        {
            return null;
        }

        if (!TryGetRenameTarget(context.Text, context.Offset, out RenameTarget target))
        {
            return null;
        }

        if (target.Kind != RenameTargetKind.ResourceKey)
        {
            return await RenameDeclarationAsync(context, target, ct).ConfigureAwait(false);
        }

        List<TextEdit> edits = new();
        HashSet<(int Offset, int Length)> seen = new();
        AddAttributeEdits(context.Text, "x:Key", target.Value, context.NewName, edits, seen);
        AddAttributeEdits(context.Text, "Key", target.Value, context.NewName, edits, seen);
        AddMarkupExtensionEdits(context.Text, "StaticResource", target.Value, context.NewName, edits, seen);
        AddMarkupExtensionEdits(context.Text, "DynamicResource", target.Value, context.NewName, edits, seen);
        if (edits.Count == 0)
        {
            return null;
        }

        return new LanguageWorkspaceEdit
        {
            DocumentEdits = new[] { new LanguageDocumentEdit { FilePath = context.FilePath, Edits = edits } }
        };
    }

    // Names and event handlers are renamed like the designer does: the name with the selectors and bindings in its
    // scope, a handler in every event attribute, and with code-behind the C# declarations and references too.
    private async Task<LanguageWorkspaceEdit?> RenameDeclarationAsync(
        LanguageRenameContext context, RenameTarget target, CancellationToken ct)
    {
        if (!IsIdentifier(context.NewName) || FindRenamedElement(context.Text, target) is not { } found)
        {
            return null;
        }

        (MutableAstDocument document, MutableAstObjectNode element) = found;
        MutableAstObjectNode root = document.Root!;
        string? className = XamlNames.FindClassName(root, document);
        bool renamesCodeBehind = className is not null;
        DesignerCodeBehindMember member = DesignerCodeBehindMember.EventHandler;
        if (target.Kind == RenameTargetKind.ElementName)
        {
            // Only names in the scope of the root element become fields of the code-behind class.
            renamesCodeBehind &= ReferenceEquals(XamlNames.ScopeRoot(element), root);
            member = DesignerCodeBehindMember.Control;
            XamlNames.PlanRename(document, element, context.NewName, _metadataService)?.Invoke();
        }
        else
        {
            foreach (MutableAstTextNode handler in XamlNames.Objects(root)
                         .SelectMany(owner => owner.Properties.Where(property => IsEvent(owner, property.PropertyName)))
                         .Select(property => property.Value).OfType<MutableAstTextNode>()
                         .Where(value => value.Text == target.Value).ToArray())
            {
                handler.Text = context.NewName;
            }
        }

        List<LanguageDocumentEdit> documentEdits = new()
        {
            new LanguageDocumentEdit
            {
                FilePath = context.FilePath,
                ExpectedText = context.Text,
                Edits = XamlSerializationService.ComputeMinimalEdits(context.Text, _serializer.Serialize(document))
            }
        };
        if (renamesCodeBehind)
        {
            DesignerCodeBehindRenameRequest request = new(null, className!, target.Value, context.NewName,
                _openDocumentTexts?.Invoke() ?? new Dictionary<string, string>())
            {
                Member = member,
                XamlFilePath = context.FilePath
            };
            LanguageWorkspaceEdit? plan = await _codeBehindRefactoring!.PrepareControlRenameAsync(request, ct).ConfigureAwait(false);
            documentEdits.AddRange(plan?.DocumentEdits ?? []);
        }

        return new LanguageWorkspaceEdit { DocumentEdits = documentEdits };
    }

    // Returns the document and the element whose attribute at the target is renamed, or null when the text has
    // errors, the attribute is neither the name of its element nor an event of its type, or the document has
    // code-behind but no C# refactoring is available.
    private (MutableAstDocument Document, MutableAstObjectNode Element)? FindRenamedElement(string text, RenameTarget target)
    {
        ParseResult result = _parser.Parse(text, new XamlParserOptions { UseTolerantParser = false });
        if (result.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ||
            result.Document is not MutableAstDocument { Root: { } root } document ||
            _codeBehindRefactoring is null && XamlNames.FindClassName(root, document) is not null)
        {
            return null;
        }

        // The attribute of the target is the last one of its name that starts before its value.
        LanguageTextPosition value = OffsetToPosition(text, target.ValueStart);
        MutableAstPropertyNode? attribute = XamlNames.Objects(root).SelectMany(element => element.Properties)
            .Where(property => property.PropertyName == target.AttributeName &&
                               (property.Line, property.Column).CompareTo((value.Line, value.Column)) < 0)
            .MaxBy(property => (property.Line, property.Column));
        if (attribute?.Parent is not MutableAstObjectNode element)
        {
            return null;
        }

        bool renamed = target.Kind == RenameTargetKind.ElementName
            ? ReferenceEquals(XamlNames.FindNameProperty(element, _metadataService), attribute)
            : IsEvent(element, attribute.PropertyName);
        return renamed ? (document, element) : null;
    }

    private bool IsEvent(MutableAstObjectNode element, string propertyName) =>
        _metadataService.GetType(element.XmlNamespace, element.TypeName) is { } type &&
        _metadataService.GetEvents(type).Any(item => item.Name == propertyName);

    public Task<LanguageSignatureHelp?> GetSignatureHelpAsync(
        LanguagePositionContext context,
        CancellationToken ct = default)
    {
        return Task.FromResult<LanguageSignatureHelp?>(null);
    }

    public Task<IReadOnlyList<LanguageCodeAction>> GetCodeActionsAsync(
        LanguageCodeActionContext context,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LanguageCodeAction>>(Array.Empty<LanguageCodeAction>());
    }

    public Task<IReadOnlyList<LanguageSymbol>> GetDocumentSymbolsAsync(
        LanguageDocumentContext context,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LanguageSymbol>>(Array.Empty<LanguageSymbol>());
    }

    public Task<IReadOnlyList<LanguageSymbol>> GetWorkspaceSymbolsAsync(
        LanguageSymbolQuery query,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<LanguageSymbol>>(Array.Empty<LanguageSymbol>());
    }

    private static string ExtractTokenAt(string text, int offset)
    {
        if (offset < 0 || offset > text.Length)
        {
            return string.Empty;
        }

        int start = offset;
        while (start > 0 && IsTokenChar(text[start - 1]))
        {
            start--;
        }

        int end = offset;
        while (end < text.Length && IsTokenChar(text[end]))
        {
            end++;
        }

        return end > start ? text.Substring(start, end - start) : string.Empty;
    }

    private static bool IsTokenChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '.' || c == ':' || c == '_';
    }

    private static IReadOnlyList<LanguageSemanticToken> BuildSemanticTokens(string text)
    {
        List<LanguageSemanticToken> tokens = new();
        List<int> lineStarts = BuildLineStarts(text);
        int length = text.Length;
        bool inTag = false;

        int i = 0;
        while (i < length)
        {
            char c = text[i];
            if (c == '<')
            {
                if (i + 3 < length && text[i + 1] == '!' && text[i + 2] == '-' && text[i + 3] == '-')
                {
                    int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    int endOffset = end < 0 ? length : Math.Min(length, end + 3);
                    AddToken(tokens, lineStarts, i, endOffset, "comment");
                    i = endOffset;
                    inTag = false;
                    continue;
                }

                inTag = true;
                int nameStart = i + 1;
                if (nameStart < length && (text[nameStart] == '/' || text[nameStart] == '?'))
                {
                    nameStart++;
                }

                while (nameStart < length && char.IsWhiteSpace(text[nameStart]))
                {
                    nameStart++;
                }

                int nameEnd = nameStart;
                while (nameEnd < length && IsTokenChar(text[nameEnd]))
                {
                    nameEnd++;
                }

                if (nameEnd > nameStart)
                {
                    AddToken(tokens, lineStarts, nameStart, nameEnd, "class");
                    i = nameEnd;
                    continue;
                }
            }

            if (inTag)
            {
                if (c == '>' )
                {
                    inTag = false;
                    i++;
                    continue;
                }

                if (c == '/' && i + 1 < length && text[i + 1] == '>')
                {
                    inTag = false;
                    i += 2;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                int attrStart = i;
                int attrEnd = attrStart;
                while (attrEnd < length && IsTokenChar(text[attrEnd]))
                {
                    attrEnd++;
                }

                if (attrEnd > attrStart)
                {
                    AddToken(tokens, lineStarts, attrStart, attrEnd, "property");
                    i = attrEnd;

                    while (i < length && char.IsWhiteSpace(text[i]))
                    {
                        i++;
                    }

                    if (i < length && text[i] == '=')
                    {
                        i++;
                        while (i < length && char.IsWhiteSpace(text[i]))
                        {
                            i++;
                        }

                        if (i < length && (text[i] == '"' || text[i] == '\''))
                        {
                            char quote = text[i];
                            int valueStart = i + 1;
                            int valueEnd = text.IndexOf(quote, valueStart);
                            if (valueEnd < 0)
                            {
                                valueEnd = length;
                            }

                            if (!TryAddMarkupExtensionTokens(tokens, lineStarts, text, valueStart, valueEnd))
                            {
                                AddToken(tokens, lineStarts, valueStart, valueEnd, "string");
                            }

                            i = Math.Min(length, valueEnd + 1);
                        }
                    }

                    continue;
                }
            }

            i++;
        }

        return tokens;
    }

    private static List<int> BuildLineStarts(string text)
    {
        List<int> starts = new() { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static void AddToken(
        List<LanguageSemanticToken> tokens,
        IReadOnlyList<int> lineStarts,
        int startOffset,
        int endOffset,
        string type)
    {
        if (endOffset <= startOffset)
        {
            return;
        }

        LanguageTextPosition start = OffsetToPosition(lineStarts, startOffset);
        LanguageTextPosition end = OffsetToPosition(lineStarts, endOffset);
        tokens.Add(new LanguageSemanticToken
        {
            Range = new LanguageTextRange(start, end),
            Type = type
        });
    }

    private static bool TryAddMarkupExtensionTokens(
        List<LanguageSemanticToken> tokens,
        IReadOnlyList<int> lineStarts,
        string text,
        int valueStart,
        int valueEnd)
    {
        if (!TryGetMarkupExtensionBounds(text, valueStart, valueEnd, out int innerStart, out int innerEnd))
        {
            return false;
        }

        int i = innerStart;
        SkipWhitespace(text, ref i, innerEnd);

        int nameStart = i;
        while (i < innerEnd && IsTokenChar(text[i]))
        {
            i++;
        }

        if (i <= nameStart)
        {
            return false;
        }

        string extensionName = text.Substring(nameStart, i - nameStart);
        AddToken(tokens, lineStarts, nameStart, i, "function");

        SkipWhitespace(text, ref i, innerEnd);

        bool parsedPositional = false;
        if (IsPositionalArgument(extensionName, text, i, innerEnd))
        {
            int valueTokenStart = i;
            while (i < innerEnd && text[i] != ',' && text[i] != '}')
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    break;
                }
                i++;
            }

            if (i > valueTokenStart)
            {
                string valueToken = text.Substring(valueTokenStart, i - valueTokenStart);
                if (!string.IsNullOrWhiteSpace(valueToken))
                {
                    AddToken(tokens, lineStarts, valueTokenStart, i, "variable");
                }
            }

            parsedPositional = true;
        }

        while (i < innerEnd)
        {
            SkipSeparators(text, ref i, innerEnd);
            int propStart = i;
            while (i < innerEnd && IsTokenChar(text[i]))
            {
                i++;
            }

            if (i <= propStart)
            {
                i++;
                continue;
            }

            int propEnd = i;
            SkipWhitespace(text, ref i, innerEnd);
            if (i < innerEnd && text[i] == '=')
            {
                AddToken(tokens, lineStarts, propStart, propEnd, "property");
                i++;
                SkipWhitespace(text, ref i, innerEnd);

                if (i >= innerEnd)
                {
                    break;
                }

                if (text[i] == '"' || text[i] == '\'')
                {
                    char quote = text[i];
                    int valueStartIndex = i + 1;
                    int valueEndIndex = text.IndexOf(quote, valueStartIndex);
                    if (valueEndIndex < 0 || valueEndIndex > innerEnd)
                    {
                        valueEndIndex = innerEnd;
                    }

                    AddToken(tokens, lineStarts, valueStartIndex, valueEndIndex, "string");
                    i = Math.Min(innerEnd, valueEndIndex + 1);
                }
                else
                {
                    int valueStartIndex = i;
                    while (i < innerEnd && text[i] != ',' && text[i] != '}')
                    {
                        if (char.IsWhiteSpace(text[i]))
                        {
                            break;
                        }
                        i++;
                    }

                    string value = text.Substring(valueStartIndex, i - valueStartIndex);
                    if (IsBindingPathProperty(extensionName, text.Substring(propStart, propEnd - propStart)))
                    {
                        AddToken(tokens, lineStarts, valueStartIndex, i, "variable");
                    }
                    else if (!string.IsNullOrWhiteSpace(value))
                    {
                        AddToken(tokens, lineStarts, valueStartIndex, i, "string");
                    }
                }
            }
        }

        return parsedPositional || tokens.Count > 0;
    }

    private static bool TryGetMarkupExtensionBounds(
        string text,
        int valueStart,
        int valueEnd,
        out int innerStart,
        out int innerEnd)
    {
        innerStart = 0;
        innerEnd = 0;

        int start = valueStart;
        while (start < valueEnd && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        int end = valueEnd;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (end - start < 2 || text[start] != '{' || text[end - 1] != '}')
        {
            return false;
        }

        innerStart = start + 1;
        innerEnd = end - 1;
        return innerEnd > innerStart;
    }

    private static bool IsPositionalArgument(string extensionName, string text, int index, int end)
    {
        SkipWhitespace(text, ref index, end);
        if (index >= end)
        {
            return false;
        }

        if (!char.IsLetterOrDigit(text[index]) && text[index] != '{')
        {
            return false;
        }

        int tokenStart = index;
        while (index < end && IsTokenChar(text[index]))
        {
            index++;
        }

        SkipWhitespace(text, ref index, end);
        if (index < end && text[index] == '=')
        {
            return false;
        }

        return IsBindingPathExtension(extensionName) || IsResourceExtension(extensionName);
    }

    private static bool IsBindingPathExtension(string extensionName)
    {
        return extensionName.Equals("Binding", StringComparison.OrdinalIgnoreCase)
            || extensionName.Equals("TemplateBinding", StringComparison.OrdinalIgnoreCase)
            || extensionName.Equals("RelativeSource", StringComparison.OrdinalIgnoreCase)
            || extensionName.Equals("CompiledBinding", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResourceExtension(string extensionName)
    {
        return extensionName.Equals("StaticResource", StringComparison.OrdinalIgnoreCase)
            || extensionName.Equals("DynamicResource", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBindingPathProperty(string extensionName, string propertyName)
    {
        if (!IsBindingPathExtension(extensionName))
        {
            return false;
        }

        return propertyName.Equals("Path", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("ElementName", StringComparison.OrdinalIgnoreCase);
    }

    private static void SkipWhitespace(string text, ref int index, int end)
    {
        while (index < end && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
    }

    private static void SkipSeparators(string text, ref int index, int end)
    {
        while (index < end && (char.IsWhiteSpace(text[index]) || text[index] == ','))
        {
            index++;
        }
    }

    private static LanguageTextPosition OffsetToPosition(IReadOnlyList<int> lineStarts, int offset)
    {
        int index = lineStarts.Count - 1;
        int lo = 0;
        int hi = lineStarts.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lineStarts[mid] <= offset)
            {
                index = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        int line = index + 1;
        int column = offset - lineStarts[index] + 1;
        return new LanguageTextPosition(line, Math.Max(1, column));
    }

    private static bool TryGetRenameTarget(string text, int offset, out RenameTarget target)
    {
        target = default;
        if (!TryGetAttributeValueSpan(text, offset, out string attributeName, out string value, out int start, out int end))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (attributeName.Equals("x:Name", StringComparison.Ordinal) ||
            attributeName.Equals("Name", StringComparison.Ordinal))
        {
            target = new RenameTarget(attributeName, value, start, end, RenameTargetKind.ElementName);
            return true;
        }

        if (attributeName.Equals("x:Key", StringComparison.Ordinal) ||
            attributeName.Equals("Key", StringComparison.Ordinal))
        {
            target = new RenameTarget(attributeName, value, start, end, RenameTargetKind.ResourceKey);
            return true;
        }

        if (IsEventAttributeName(attributeName) && IsIdentifier(value))
        {
            target = new RenameTarget(attributeName, value, start, end, RenameTargetKind.EventHandler);
            return true;
        }

        return false;
    }

    private static bool TryGetAttributeValueSpan(
        string text,
        int offset,
        out string attributeName,
        out string value,
        out int valueStart,
        out int valueEnd)
    {
        attributeName = string.Empty;
        value = string.Empty;
        valueStart = 0;
        valueEnd = 0;

        if (offset < 0 || offset > text.Length)
        {
            return false;
        }

        int quoteStart = FindQuoteStart(text, offset, out char quote);
        if (quoteStart < 0)
        {
            return false;
        }

        int quoteEnd = text.IndexOf(quote, quoteStart + 1);
        if (quoteEnd <= quoteStart)
        {
            return false;
        }

        if (offset < quoteStart || offset > quoteEnd)
        {
            return false;
        }

        if (!TryGetAttributeNameBefore(text, quoteStart, out attributeName))
        {
            return false;
        }

        valueStart = quoteStart + 1;
        valueEnd = quoteEnd;
        value = valueEnd > valueStart ? text.Substring(valueStart, valueEnd - valueStart) : string.Empty;
        return true;
    }

    private static int FindQuoteStart(string text, int offset, out char quote)
    {
        quote = '\0';
        for (int i = Math.Min(offset, text.Length - 1); i >= 0; i--)
        {
            char c = text[i];
            if (c == '"' || c == '\'')
            {
                quote = c;
                return i;
            }
        }

        return -1;
    }

    private static bool TryGetAttributeNameBefore(string text, int quoteStart, out string attributeName)
    {
        attributeName = string.Empty;
        int index = quoteStart - 1;
        while (index >= 0 && char.IsWhiteSpace(text[index]))
        {
            index--;
        }

        if (index < 0 || text[index] != '=')
        {
            return false;
        }

        index--;
        while (index >= 0 && char.IsWhiteSpace(text[index]))
        {
            index--;
        }

        int end = index;
        while (index >= 0 && IsAttributeNameChar(text[index]))
        {
            index--;
        }

        int start = index + 1;
        if (end < start)
        {
            return false;
        }

        attributeName = text.Substring(start, end - start + 1);
        return !string.IsNullOrWhiteSpace(attributeName);
    }

    private static bool IsAttributeNameChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == ':' || c == '.' || c == '_';
    }

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!(char.IsLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        return value.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static bool IsEventAttributeName(string attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName))
        {
            return false;
        }

        if (attributeName.Contains(':', StringComparison.Ordinal) || attributeName.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        if (!char.IsUpper(attributeName[0]))
        {
            return false;
        }

        return !IsDirectiveAttribute(attributeName);
    }

    private static bool IsDirectiveAttribute(string attributeName)
    {
        return attributeName is "Name" or "Key" or "Class" or "DataContext" or "Styles";
    }

    private static void AddAttributeEdits(
        string text,
        string attributeName,
        string oldValue,
        string newValue,
        List<TextEdit> edits,
        HashSet<(int Offset, int Length)> seen)
    {
        AddAttributeEditsForQuote(text, attributeName, oldValue, newValue, '"', edits, seen);
        AddAttributeEditsForQuote(text, attributeName, oldValue, newValue, '\'', edits, seen);
    }

    private static void AddAttributeEditsForQuote(
        string text,
        string attributeName,
        string oldValue,
        string newValue,
        char quote,
        List<TextEdit> edits,
        HashSet<(int Offset, int Length)> seen)
    {
        string marker = attributeName + "=" + quote;
        int index = 0;
        while (index < text.Length)
        {
            int attrIndex = text.IndexOf(marker, index, StringComparison.Ordinal);
            if (attrIndex < 0)
            {
                break;
            }

            int start = attrIndex + marker.Length;
            int end = text.IndexOf(quote, start);
            if (end <= start)
            {
                break;
            }

            string value = text.Substring(start, end - start);
            if (string.Equals(value, oldValue, StringComparison.Ordinal))
            {
                AddEdit(edits, seen, start, end - start, newValue);
            }

            index = end + 1;
        }
    }

    private static void AddMarkupExtensionEdits(
        string text,
        string extensionName,
        string oldValue,
        string newValue,
        List<TextEdit> edits,
        HashSet<(int Offset, int Length)> seen)
    {
        string marker = "{" + extensionName;
        int index = 0;
        while (index < text.Length)
        {
            int startIndex = text.IndexOf(marker, index, StringComparison.Ordinal);
            if (startIndex < 0)
            {
                break;
            }

            int contentStart = startIndex + marker.Length;
            int endBrace = text.IndexOf('}', contentStart);
            if (endBrace < 0)
            {
                break;
            }

            string rawContent = text.Substring(contentStart, endBrace - contentStart);
            int leadingWhitespace = rawContent.Length - rawContent.TrimStart().Length;
            string content = rawContent.TrimStart();
            if (TryGetMarkupExtensionToken(content, oldValue, out int tokenOffset, out int tokenLength))
            {
                int absoluteOffset = contentStart + leadingWhitespace + tokenOffset;
                AddEdit(edits, seen, absoluteOffset, tokenLength, newValue);
            }

            index = endBrace + 1;
        }
    }

    private static bool TryGetMarkupExtensionToken(string content, string oldValue, out int offset, out int length)
    {
        offset = 0;
        length = 0;

        if (content.StartsWith(oldValue, StringComparison.Ordinal))
        {
            offset = 0;
            length = oldValue.Length;
            return true;
        }

        string[] keys = new[] { "ResourceKey=", "Key=" };
        foreach (string key in keys)
        {
            int keyIndex = content.IndexOf(key, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                continue;
            }

            int valueStart = keyIndex + key.Length;
            int valueEnd = FindTokenEnd(content, valueStart);
            if (valueEnd <= valueStart)
            {
                continue;
            }

            string value = content.Substring(valueStart, valueEnd - valueStart);
            if (string.Equals(value, oldValue, StringComparison.Ordinal))
            {
                offset = valueStart;
                length = value.Length;
                return true;
            }
        }

        return false;
    }

    private static int FindTokenEnd(string text, int start)
    {
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c) || c == ',' || c == '}' || c == '\'' || c == '"')
            {
                return i;
            }
        }

        return text.Length;
    }

    private static void AddEdit(
        List<TextEdit> edits,
        HashSet<(int Offset, int Length)> seen,
        int offset,
        int length,
        string newValue)
    {
        if (offset < 0 || length < 0)
        {
            return;
        }

        if (seen.Add((offset, length)))
        {
            edits.Add(new TextEdit
            {
                Offset = offset,
                Length = length,
                NewText = newValue
            });
        }
    }

    private static LanguageTextPosition OffsetToPosition(string text, int offset)
    {
        int line = 1;
        int column = 1;
        int max = Math.Clamp(offset, 0, text.Length);
        for (int i = 0; i < max; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return new LanguageTextPosition(line, column);
    }

    private readonly record struct RenameTarget(
        string AttributeName,
        string Value,
        int ValueStart,
        int ValueEnd,
        RenameTargetKind Kind);

    private enum RenameTargetKind
    {
        None,
        ElementName,
        ResourceKey,
        EventHandler
    }
}
