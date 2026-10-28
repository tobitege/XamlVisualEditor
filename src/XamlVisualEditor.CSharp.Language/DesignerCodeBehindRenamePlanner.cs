using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.CSharp.Language;

/// <summary>
/// Plans semantic edits of control members with their typed Avalonia lookups, or of event handler methods, without
/// changing source documents.
/// </summary>
public static class DesignerCodeBehindRenamePlanner
{
    /// <summary>
    /// Finds references to a control member in editable documents, excluding generated source trees and unrelated
    /// symbols or literals. A null new name checks whether removing a declaration would leave C# references behind.
    /// </summary>
    public static Task<LanguageWorkspaceEdit?> CreateAsync(Project project, string className, string oldName,
        string? newName, CancellationToken cancellationToken = default) =>
        CreateAsync(project, className, oldName, newName, DesignerCodeBehindMember.Control, cancellationToken);

    /// <summary>
    /// Plans the rename of a request in the solution with the unsaved texts of the request. The project of the
    /// request owns the class, or else the one project that lists its AXAML file among its additional files.
    /// </summary>
    public static async Task<LanguageWorkspaceEdit?> CreateAsync(Solution solution, DesignerCodeBehindRenameRequest request,
        CancellationToken cancellationToken = default)
    {
        foreach (KeyValuePair<string, string> openDocument in request.OpenDocumentTexts)
            foreach (DocumentId id in solution.GetDocumentIdsWithFilePath(openDocument.Key))
                solution = solution.WithDocumentText(id, SourceText.From(openDocument.Value));
        // A project with several target frameworks loads once per framework, all sharing the same sources.
        Project[] owners = solution.Projects
            .Where(project => request.ProjectPath is not null
                ? FileSystemPathComparison.Equals(project.FilePath, request.ProjectPath)
                : project.AdditionalDocuments.Any(document => FileSystemPathComparison.Equals(document.FilePath, request.XamlFilePath)))
            .DistinctBy(project => project.FilePath, FileSystemPathComparison.Comparer)
            .ToArray();
        if (owners.Length > 1) throw new InvalidOperationException(UiStrings.CodeBehindRenameMultipleProjects);
        Project owner = owners.Length == 1 ? owners[0]
            : throw new InvalidOperationException(UiStrings.CodeBehindRenameOwningProjectMissing);
        return await CreateAsync(owner, request.ClassName, request.OldName, request.NewName, request.Member,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LanguageWorkspaceEdit?> CreateAsync(Project project, string className, string oldName,
        string? newName, DesignerCodeBehindMember member, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (newName == oldName) return null;
        if (newName is not null && (newName.StartsWith('@') || !SyntaxFacts.IsValidIdentifier(newName)
                || SyntaxFacts.GetKeywordKind(newName) != SyntaxKind.None))
            throw new InvalidOperationException(UiStrings.CodeBehindRenameInvalidName);
        Compilation compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(UiStrings.CodeBehindRenameCompilationUnavailable);
        INamedTypeSymbol owner = compilation.GetTypeByMetadataName(className)
            ?? throw new InvalidOperationException(string.Format(UiStrings.CodeBehindRenameClassMissing, className));
        if (!SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, compilation.Assembly))
            throw new InvalidOperationException(UiStrings.CodeBehindRenameClassForeign);
        if (newName is not null && owner.GetMembers(newName).Length != 0)
            throw new InvalidOperationException(string.Format(UiStrings.CodeBehindRenameMemberExists, newName));

        Solution solution = project.Solution;
        Dictionary<DocumentId, Document> documents = solution.Projects.SelectMany(candidate => candidate.Documents)
            .Where(document => !string.IsNullOrWhiteSpace(document.FilePath)).ToDictionary(document => document.Id);
        Dictionary<string, PendingDocument> pending = new(FileSystemPathComparison.Comparer);
        Func<ISymbol, bool> isRenamed = member == DesignerCodeBehindMember.Control ? IsControlMember : symbol => symbol is IMethodSymbol;
        foreach (ISymbol symbol in owner.GetMembers(oldName).Where(isRenamed))
        {
            foreach (Location location in symbol.Locations) await AddLocationAsync(location).ConfigureAwait(false);
            IEnumerable<ReferencedSymbol> references = await SymbolFinder.FindReferencesAsync(symbol, solution,
                cancellationToken).ConfigureAwait(false);
            foreach (ReferencedSymbol reference in references)
                foreach (ReferenceLocation location in reference.Locations)
                    await AddLocationAsync(location.Location).ConfigureAwait(false);
        }

        // Controls are also looked up by name; handlers are only referenced as methods.
        foreach (Document document in member == DesignerCodeBehindMember.Control ? documents.Values : Enumerable.Empty<Document>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) continue;
            InvocationExpressionSyntax[] lookups = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax access
                    && access.Name.Identifier.ValueText is "FindControl" or "Find").ToArray();
            if (lookups.Length == 0) continue;
            SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is null) continue;
            foreach (InvocationExpressionSyntax lookup in lookups)
            {
                MemberAccessExpressionSyntax access = (MemberAccessExpressionSyntax)lookup.Expression;
                if (model.GetTypeInfo(access.Expression, cancellationToken).Type is not INamedTypeSymbol receiver
                    || !SameType(receiver, owner)
                    || model.GetSymbolInfo(lookup, cancellationToken).Symbol is not IMethodSymbol method
                    || method.ContainingNamespace.ToDisplayString() != "Avalonia.Controls") continue;
                int nameIndex = -1;
                for (int index = 0; index < method.Parameters.Length; index++)
                    if (method.Parameters[index].Name == "name") { nameIndex = index; break; }
                if (nameIndex < 0) continue;
                ArgumentSyntax? argument = lookup.ArgumentList.Arguments.FirstOrDefault(candidate =>
                    candidate.NameColon?.Name.Identifier.ValueText == "name");
                argument ??= nameIndex < lookup.ArgumentList.Arguments.Count
                    ? lookup.ArgumentList.Arguments[nameIndex] : null;
                if (argument?.Expression is not LiteralExpressionSyntax literal
                    || !literal.IsKind(SyntaxKind.StringLiteralExpression) || literal.Token.ValueText != oldName) continue;
                await AddEditAsync(document, literal.Span,
                    newName is null ? string.Empty : SyntaxFactory.Literal(newName).ToString()).ConfigureAwait(false);
            }
        }

        if (pending.Count == 0) return null;
        if (newName is null) throw new InvalidOperationException(string.Format(UiStrings.CodeBehindRenameNameReferenced, oldName));
        return new LanguageWorkspaceEdit
        {
            DocumentEdits = pending.OrderBy(pair => pair.Key, FileSystemPathComparison.Comparer).Select(pair =>
                new LanguageDocumentEdit
                {
                    FilePath = pair.Key,
                    ExpectedText = pair.Value.Text,
                    Edits = pair.Value.Edits.Values.OrderByDescending(edit => edit.Offset).ToArray()
                }).ToArray()
        };

        async Task AddLocationAsync(Location location)
        {
            if (!location.IsInSource || location.SourceTree is null) return;
            DocumentId? id = solution.GetDocumentId(location.SourceTree);
            if (id is not null && documents.TryGetValue(id, out Document? document))
                await AddEditAsync(document, location.SourceSpan, newName ?? string.Empty).ConfigureAwait(false);
        }

        async Task AddEditAsync(Document document, TextSpan span, string replacement)
        {
            string path = document.FilePath!;
            if (!pending.TryGetValue(path, out PendingDocument? entry))
            {
                SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                pending.Add(path, entry = new PendingDocument(text.ToString()));
            }
            if (entry.Edits.TryGetValue(span, out TextEdit? existing) && existing.NewText != replacement)
                throw new InvalidOperationException(string.Format(UiStrings.CodeBehindRenameConflictingEdits, path));
            entry.Edits[span] = new TextEdit { Offset = span.Start, Length = span.Length, NewText = replacement };
        }
    }

    private static bool IsControlMember(ISymbol member)
    {
        ITypeSymbol? type = member switch { IFieldSymbol field => field.Type, IPropertySymbol property => property.Type, _ => null };
        for (INamedTypeSymbol? current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == "Avalonia.Controls.Control") return true;
        return false;
    }

    private static bool SameType(INamedTypeSymbol left, INamedTypeSymbol right) =>
        left.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            == right.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        && left.ContainingAssembly.Identity.Equals(right.ContainingAssembly.Identity);

    private sealed class PendingDocument(string text)
    {
        public string Text { get; } = text;
        public Dictionary<TextSpan, TextEdit> Edits { get; } = new();
    }
}
