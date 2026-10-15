using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class MainWindowViewModel
{
    internal async Task<bool> SetDesignerEventHandlerAsync(
        DesignerDocumentViewModel document, MutableAstObjectNode node, string eventName,
        string? handlerName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        handlerName = string.IsNullOrWhiteSpace(handlerName) ? null : handlerName.Trim();
        if (handlerName is not null)
        {
            await ResolveDesignerEventAsync(document, node, eventName, handlerName, cancellationToken);
        }

        if (string.Equals(node.GetPropertyValue(eventName), handlerName, StringComparison.Ordinal))
        {
            return true;
        }

        using var transaction = document.SyncEngine.BeginEditTransaction($"Set {eventName}");
        node.SetPropertyValue(eventName, handlerName);
        transaction.Commit();
        return true;
    }

    internal async Task<bool> NavigateToDesignerEventHandlerAsync(
        DesignerDocumentViewModel document, MutableAstObjectNode node, string eventName,
        CancellationToken cancellationToken)
    {
        string? handlerName = node.GetPropertyValue(eventName);
        if (string.IsNullOrWhiteSpace(handlerName))
        {
            throw new InvalidOperationException("Assign an event handler before opening its definition.");
        }

        DesignerEventBindingResult binding = await ResolveDesignerEventAsync(
            document, node, eventName, handlerName, cancellationToken);
        if (binding.FilePath is null || binding.ExpectedText is null)
        {
            throw new InvalidOperationException("This handler is declared in assembly metadata and has no editable source definition.");
        }

        if (await EnsureDocumentOpenAsync(binding.FilePath, addRecent: true, updateStatus: true, allowWorkspaceLoad: false)
            is not TextDocumentViewModel target || target.IsDisposed ||
            !string.Equals(target.Document.Text, binding.ExpectedText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The handler source changed while opening its definition. Retry navigation.");
        }

        target.SetSelection(binding.NameOffset, binding.NameLength, binding.NameOffset);
        return true;
    }

    private async Task<DesignerEventBindingResult> ResolveDesignerEventAsync(
        DesignerDocumentViewModel document, MutableAstObjectNode node, string eventName, string handlerName,
        CancellationToken cancellationToken)
    {
        MutableAstDocument ast = document.SyncEngine.CurrentDocument
            ?? throw new InvalidOperationException("The AXAML document is not synchronized.");
        MutableAstObjectNode root = ast.Root
            ?? throw new InvalidOperationException("The AXAML document has no root control.");
        string className = GetDesignerClassName(root, ast)
            ?? throw new InvalidOperationException("An event handler requires an x:Class with a matching C# form class.");
        TypeMetadata controlType = document.MetadataService?.GetType(node.XmlNamespace, node.TypeName)
            ?? throw new InvalidOperationException("The selected control's type could not be resolved.");
        ProjectModel[] owners = _workspace?.Projects
            .Where(project => project.XamlFiles.Any(path => FileSystemPathComparison.Equals(path, document.FilePath)))
            .ToArray() ?? [];
        if (owners.Length > 1)
        {
            throw new InvalidOperationException("The AXAML file belongs to multiple projects. Open its owning project first.");
        }

        IDesignerEventBindingService service = _languageRegistry?.Services.OfType<IDesignerEventBindingService>().FirstOrDefault()
            ?? throw new InvalidOperationException("The loaded project has no C# event-binding service.");
        WorkspaceModel? workspace = _workspace;
        string originalXaml = document.SyncEngine.CurrentText
            ?? throw new InvalidOperationException("Wait for the AXAML document to finish synchronizing.");
        Dictionary<string, string> openTexts = Documents.OfType<TextDocumentViewModel>()
            .Where(item => !item.IsDisposed && string.Equals(Path.GetExtension(item.FilePath), ".cs", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.FilePath, item => item.Document.Text, FileSystemPathComparison.Comparer);
        DesignerEventBindingRequest request = new(owners.SingleOrDefault()?.ProjectPath, className,
            controlType.FullName, controlType.AssemblyName, eventName, handlerName, openTexts);
        DesignerEventBindingResult result = await service.ResolveEventHandlerAsync(request, cancellationToken);

        if (result.FilePath is not null && result.ExpectedText is not null)
        {
            TextDocumentViewModel? openSource = Documents.OfType<TextDocumentViewModel>()
                .FirstOrDefault(item => FileSystemPathComparison.Equals(item.FilePath, result.FilePath));
            string currentSource = openSource is null
                ? await File.ReadAllTextAsync(result.FilePath, cancellationToken)
                : openSource.Document.Text;
            if (!string.Equals(currentSource, result.ExpectedText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The handler source changed during validation. Reload it and retry the assignment.");
            }
        }

        EnsureDesignerEditContext(document, node, workspace, originalXaml, cancellationToken);
        foreach (var snapshot in openTexts)
        {
            TextDocumentViewModel? current = Documents.OfType<TextDocumentViewModel>()
                .FirstOrDefault(item => FileSystemPathComparison.Equals(item.FilePath, snapshot.Key));
            if (current is null || current.IsDisposed || !string.Equals(current.Document.Text, snapshot.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A C# document changed during event validation. Retry the assignment.");
            }
        }

        if (Documents.OfType<TextDocumentViewModel>().Any(item => item.IsModified &&
            string.Equals(Path.GetExtension(item.FilePath), ".cs", StringComparison.OrdinalIgnoreCase) && !openTexts.ContainsKey(item.FilePath)))
        {
            throw new InvalidOperationException("A C# document was edited during event validation. Retry the assignment.");
        }

        return result;
    }
}
