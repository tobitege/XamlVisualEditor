using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class MainWindowViewModel
{
    internal async Task<bool> RenameDesignerControlAsync(
        DesignerDocumentViewModel document, MutableAstObjectNode node, string? value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MutableAstDocument? ast = document.SyncEngine.CurrentDocument;
        MutableAstObjectNode? root = ast?.Root;
        MutableAstPropertyNode? nameProperty = DesignerNameEditor.FindNameProperty(node, document.MetadataService);
        string? oldName = nameProperty is null ? null : node.GetPropertyValue(nameProperty.PropertyName);
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        ProjectModel[] owners = _workspace?.Projects
            .Where(project => project.XamlFiles.Any(path => FileSystemPathComparison.Equals(path, document.FilePath)))
            .ToArray() ?? [];
        if (root is null || string.IsNullOrEmpty(oldName) || owners.Length == 0 ||
            !ReferenceEquals(DesignerNameEditor.ScopeRoot(node), root) ||
            string.Equals(oldName, value, StringComparison.Ordinal))
        {
            DesignerNameEditor.Rename(document, node, value);
            return true;
        }

        string? className = GetDesignerClassName(root, ast!);
        if (string.IsNullOrWhiteSpace(className))
        {
            DesignerNameEditor.Rename(document, node, value);
            return true;
        }

        if (owners.Length != 1)
        {
            throw new InvalidOperationException(UiStrings.CodeBehindRenameMultipleProjects);
        }

        IDesignerCodeBehindRefactoringService service = _languageRegistry?.Services
            .OfType<IDesignerCodeBehindRefactoringService>().FirstOrDefault()
            ?? throw new InvalidOperationException(UiStrings.CodeBehindRenameServiceMissing);
        WorkspaceModel? workspace = _workspace;
        string originalXaml = document.SyncEngine.CurrentText
            ?? throw new InvalidOperationException(UiStrings.CodeBehindRenameSynchronizing);
        StringComparer pathComparer = FileSystemPathComparison.Comparer;
        Dictionary<string, string> openTexts = Documents.OfType<TextDocumentViewModel>()
            .Where(item => !item.IsDisposed)
            .ToDictionary(item => item.FilePath, item => item.Document.Text, pathComparer);
        DesignerCodeBehindRenameRequest request = new(owners[0].ProjectPath, className, oldName, value, openTexts);
        LanguageWorkspaceEdit? plan = await service.PrepareControlRenameAsync(request, cancellationToken);
        EnsureDesignerEditContext(document, node, workspace, originalXaml, cancellationToken);
        if (plan is null || plan.DocumentEdits.Count == 0)
        {
            DesignerNameEditor.Rename(document, node, value);
            return true;
        }

        List<TextDocumentViewModel> createdDocuments = new();
        List<PreparedDesignerCodeEdit> prepared = new();
        HashSet<string> editedPaths = new(pathComparer);
        try
        {
            foreach (LanguageDocumentEdit edit in plan.DocumentEdits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!editedPaths.Add(edit.FilePath) || edit.ExpectedText is null ||
                    !string.Equals(Path.GetExtension(edit.FilePath), ".cs", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(UiStrings.CodeBehindRenameInvalidEdit);
                }

                TextEdit[] edits = edit.Edits.ToArray();
                string after = ApplyDesignerCodeEdits(edit.ExpectedText, edits);
                if (string.Equals(edit.ExpectedText, after, StringComparison.Ordinal))
                {
                    continue;
                }

                TextDocumentViewModel? codeDocument = Documents.OfType<TextDocumentViewModel>()
                    .FirstOrDefault(item => FileSystemPathComparison.Equals(item.FilePath, edit.FilePath));
                if (codeDocument is null)
                {
                    if (!File.Exists(edit.FilePath))
                    {
                        throw new FileNotFoundException(UiStrings.CodeBehindRenameDocumentMissing, edit.FilePath);
                    }

                    codeDocument = new TextDocumentViewModel(edit.FilePath, _languageRegistry)
                    {
                        Breakpoints = Breakpoints
                    };
                    createdDocuments.Add(codeDocument);
                    await codeDocument.LoadAsync();
                }

                prepared.Add(new PreparedDesignerCodeEdit(codeDocument, edit.ExpectedText, after, edits));
            }

            EnsureDesignerEditContext(document, node, workspace, originalXaml, cancellationToken);
            foreach (PreparedDesignerCodeEdit edit in prepared)
            {
                if (edit.Document.IsDisposed || !string.Equals(edit.Document.Document.Text, edit.Before, StringComparison.Ordinal) ||
                    Documents.OfType<TextDocumentViewModel>().Any(item => !ReferenceEquals(item, edit.Document) &&
                        FileSystemPathComparison.Equals(item.FilePath, edit.Document.FilePath)))
                {
                    throw new InvalidOperationException(string.Format(UiStrings.CodeBehindRenameDocumentChanged, edit.Document.FilePath));
                }
            }

            DocumentCompanionChange? transactionAnchor = null;
            void ReplayTransaction(bool forward)
            {
                if (document.IsDisposed || transactionAnchor is null)
                {
                    throw new InvalidOperationException(UiStrings.CodeBehindRenameDesignerClosed);
                }

                if (forward)
                {
                    document.SyncEngine.Redo(transactionAnchor);
                }
                else
                {
                    document.SyncEngine.Undo(transactionAnchor);
                }
            }

            List<DocumentCompanionChange> changes = new();
            foreach (PreparedDesignerCodeEdit edit in prepared)
            {
                changes.Add(edit.Document.CreateLinkedChange(edit.Before, edit.After, edit.Edits, ReplayTransaction));
            }

            transactionAnchor = changes.FirstOrDefault();
            foreach (TextDocumentViewModel codeDocument in createdDocuments)
            {
                Documents.Add(codeDocument);
                AddDocumentToDock(codeDocument);
                AttachAutoSave(codeDocument);
            }

            ActiveDocument = document;
            DesignerNameEditor.RenameWithCodeBehind(document, node, value, changes);
            StatusText = UiStrings.ControlReferencesRenamed;
            return true;
        }
        finally
        {
            foreach (TextDocumentViewModel created in createdDocuments)
            {
                if (!Documents.Contains(created))
                {
                    created.Dispose();
                }
            }
        }
    }

    private void EnsureDesignerEditContext(
        DesignerDocumentViewModel document, MutableAstObjectNode node, WorkspaceModel? workspace,
        string originalXaml, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (document.IsDisposed || !document.SyncEngine.CanEditAst ||
            !ReferenceEquals(ActiveDesignerDocument, document) || !ReferenceEquals(_workspace, workspace) ||
            !ReferenceEquals(document.NodeMap.FindById(node.Id), node) ||
            !string.Equals(document.SyncEngine.CurrentText, originalXaml, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(UiStrings.CodeBehindRenameDesignerChanged);
        }
    }

    private static string? GetDesignerClassName(MutableAstObjectNode root, MutableAstDocument document)
    {
        IReadOnlyDictionary<string, string> aliases = DesignerNameEditor.Namespaces(root, document.NamespaceAliases);
        foreach (MutableAstPropertyNode property in root.Properties)
        {
            int separator = property.PropertyName.IndexOf(':');
            string localName = separator < 0 ? property.PropertyName : property.PropertyName[(separator + 1)..];
            string? xmlNamespace = property.XmlNamespace;
            if (separator >= 0 && aliases.TryGetValue(property.PropertyName[..separator], out string? mappedNamespace))
            {
                xmlNamespace = mappedNamespace;
            }

            if (localName == "Class" && xmlNamespace == DesignerNameEditor.XamlNamespace)
            {
                return root.GetPropertyValue(property.PropertyName);
            }
        }

        return null;
    }

    private static string ApplyDesignerCodeEdits(string text, IReadOnlyList<TextEdit> edits)
    {
        int previousOffset = -1;
        string result = text;
        foreach (TextEdit edit in edits.OrderByDescending(item => item.Offset))
        {
            if (edit.NewText is null || edit.Offset < 0 || edit.Length < 0 || edit.Offset > text.Length - edit.Length ||
                previousOffset >= 0 && (edit.Offset == previousOffset || edit.Offset + edit.Length > previousOffset))
            {
                throw new InvalidOperationException(UiStrings.CodeBehindRenameOverlappingEdits);
            }

            result = result.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.NewText);
            previousOffset = edit.Offset;
        }

        return result;
    }

    private sealed record PreparedDesignerCodeEdit(
        TextDocumentViewModel Document, string Before, string After, IReadOnlyList<TextEdit> Edits);
}
