using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.CSharp.Language;

public static class DesignerEventBindingResolver
{
    public static async Task<DesignerEventBindingResult> ResolveAsync(
        Solution solution, DesignerEventBindingRequest request, CancellationToken cancellationToken = default)
    {
        if (!SyntaxFacts.IsValidIdentifier(request.HandlerName) ||
            request.HandlerName.StartsWith('@') || SyntaxFacts.GetKeywordKind(request.HandlerName) != SyntaxKind.None)
        {
            throw new InvalidOperationException(UiStrings.EventHandlerNameInvalid);
        }

        List<(Compilation Compilation, INamedTypeSymbol Owner)> candidates = new();
        // A project with several target frameworks loads once per framework; one of them stands for the project.
        HashSet<string> candidateProjectFiles = new(FileSystemPathComparison.Comparer);
        foreach (Project project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.Language != LanguageNames.CSharp || request.ProjectPath is not null &&
                !FileSystemPathComparison.Equals(project.FilePath, request.ProjectPath) ||
                project.FilePath is { } projectFile && candidateProjectFiles.Contains(projectFile))
            {
                continue;
            }

            Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            INamedTypeSymbol? owner = compilation?.GetTypeByMetadataName(request.ClassName);
            if (compilation is not null && owner is not null &&
                SymbolEqualityComparer.Default.Equals(owner.ContainingAssembly, compilation.Assembly))
            {
                candidates.Add((compilation, owner));
                if (project.FilePath is not null) candidateProjectFiles.Add(project.FilePath);
            }
        }

        if (candidates.Count != 1)
        {
            throw new InvalidOperationException(candidates.Count == 0
                ? string.Format(UiStrings.EventHandlerFormClassMissing, request.ClassName)
                : string.Format(UiStrings.EventHandlerFormClassAmbiguous, request.ClassName));
        }

        (Compilation originalCompilation, INamedTypeSymbol ownerType) = candidates[0];
        INamedTypeSymbol controlType = ResolveControlType(originalCompilation, request)
            ?? throw new InvalidOperationException(string.Format(UiStrings.EventHandlerControlTypeMissing, request.ControlTypeFullName));
        if (controlType.IsGenericType)
        {
            throw new InvalidOperationException(UiStrings.EventHandlerGenericControl);
        }

        IEventSymbol? eventSymbol = null;
        for (INamedTypeSymbol? current = controlType; current is not null && eventSymbol is null; current = current.BaseType)
        {
            eventSymbol = current.GetMembers(request.EventName).OfType<IEventSymbol>().FirstOrDefault();
        }

        if (eventSymbol is null)
        {
            throw new InvalidOperationException(string.Format(UiStrings.EventHandlerEventMissing, request.EventName));
        }

        ClassDeclarationSyntax? ownerDeclaration = null;
        foreach (SyntaxReference reference in ownerType.DeclaringSyntaxReferences)
        {
            if (await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false) is ClassDeclarationSyntax declaration)
            {
                ownerDeclaration = declaration;
                break;
            }
        }

        if (ownerDeclaration is null)
        {
            throw new InvalidOperationException(UiStrings.EventHandlerFormDeclarationMissing);
        }

        SyntaxTree sourceTree = ownerDeclaration.SyntaxTree;
        SyntaxNode sourceRoot = await sourceTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        Compilation probeCompilation = originalCompilation;
        string controlTypeName = controlType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string? assemblyAlias = null;
        if (!SymbolEqualityComparer.Default.Equals(controlType.ContainingAssembly, originalCompilation.Assembly))
        {
            MetadataReference controlReference = originalCompilation.References.Single(reference =>
                SymbolEqualityComparer.Default.Equals(originalCompilation.GetAssemblyOrModuleSymbol(reference), controlType.ContainingAssembly));
            assemblyAlias = "__XveControl_" + Guid.NewGuid().ToString("N");
            ImmutableArray<string> aliases = controlReference.Properties.Aliases.IsDefaultOrEmpty
                ? ImmutableArray.Create("global", assemblyAlias)
                : controlReference.Properties.Aliases.Add(assemblyAlias);
            probeCompilation = probeCompilation.ReplaceReference(controlReference, controlReference.WithAliases(aliases));
            controlTypeName = assemblyAlias + "::" + controlTypeName["global::".Length..];
        }

        string eventIdentifier = SyntaxFacts.GetKeywordKind(eventSymbol.Name) == SyntaxKind.None
            ? eventSymbol.Name : "@" + eventSymbol.Name;
        string probeName = "__XveEvent_" + Guid.NewGuid().ToString("N");
        string probeText = $"private void {probeName}({controlTypeName} target) {{ target.{eventIdentifier} += {request.HandlerName}; }}";
        SyntaxAnnotation marker = new();
        MethodDeclarationSyntax probe = (SyntaxFactory.ParseMemberDeclaration(probeText) as MethodDeclarationSyntax
            ?? throw new InvalidOperationException(UiStrings.EventHandlerSignatureUnrepresentable))
            .WithAdditionalAnnotations(marker);
        SyntaxNode changedRoot = sourceRoot.ReplaceNode(ownerDeclaration, ownerDeclaration.AddMembers(probe));
        if (assemblyAlias is not null && changedRoot is CompilationUnitSyntax compilationUnit)
        {
            changedRoot = compilationUnit.AddExterns(SyntaxFactory.ExternAliasDirective(SyntaxFactory.Identifier(assemblyAlias)));
        }

        SyntaxTree changedTree = sourceTree.WithRootAndOptions(changedRoot, sourceTree.Options);
        probeCompilation = probeCompilation.ReplaceSyntaxTree(sourceTree, changedTree);
        SyntaxNode resolvedRoot = await changedTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        MethodDeclarationSyntax resolvedProbe = resolvedRoot.GetAnnotatedNodes(marker).OfType<MethodDeclarationSyntax>().Single();
        SemanticModel semanticModel = probeCompilation.GetSemanticModel(changedTree);
        Diagnostic? error = semanticModel.GetDiagnostics(resolvedProbe.Span, cancellationToken)
            .FirstOrDefault(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        if (error is not null)
        {
            throw new InvalidOperationException(string.Format(UiStrings.EventHandlerAssignmentError,
                request.HandlerName, request.EventName, error.GetMessage()));
        }

        AssignmentExpressionSyntax assignment = resolvedProbe.DescendantNodes().OfType<AssignmentExpressionSyntax>().Single();
        if (semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol is not IEventSymbol ||
            semanticModel.GetSymbolInfo(assignment.Right, cancellationToken).Symbol is not IMethodSymbol handler ||
            handler.MethodKind != MethodKind.Ordinary)
        {
            throw new InvalidOperationException(UiStrings.EventHandlerIncompatibleMethod);
        }

        string? declarationId = handler.OriginalDefinition.GetDocumentationCommentId();
        IMethodSymbol? originalHandler = declarationId is null ? null :
            DocumentationCommentId.GetFirstSymbolForDeclarationId(declarationId, originalCompilation) as IMethodSymbol;
        if (originalHandler is null)
        {
            throw new InvalidOperationException(UiStrings.EventHandlerMappingFailed);
        }

        originalHandler = originalHandler.PartialImplementationPart ?? originalHandler;
        foreach (SyntaxReference reference in originalHandler.DeclaringSyntaxReferences)
        {
            Document? document = solution.GetDocument(reference.SyntaxTree);
            if (document?.FilePath is null ||
                await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false) is not MethodDeclarationSyntax method)
            {
                continue;
            }

            SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            return new DesignerEventBindingResult(document.FilePath, text.ToString(), method.Identifier.SpanStart, method.Identifier.Span.Length);
        }

        return new DesignerEventBindingResult(null, null, 0, 0);
    }

    private static INamedTypeSymbol? ResolveControlType(Compilation compilation, DesignerEventBindingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ControlAssemblyName))
        {
            return compilation.GetTypeByMetadataName(request.ControlTypeFullName);
        }

        IAssemblySymbol[] assemblies = compilation.SourceModule.ReferencedAssemblySymbols
            .Append(compilation.Assembly)
            .Where(assembly => string.Equals(assembly.Identity.Name, request.ControlAssemblyName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return assemblies.Length == 1 ? assemblies[0].GetTypeByMetadataName(request.ControlTypeFullName) : null;
    }
}
