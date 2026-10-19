using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
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
            throw new InvalidOperationException(UiStrings.EventHandlerAssignFirst);
        }

        DesignerEventBindingResult binding = await ResolveDesignerEventAsync(
            document, node, eventName, handlerName, cancellationToken);
        if (binding.FilePath is null || binding.ExpectedText is null)
        {
            throw new InvalidOperationException(UiStrings.EventHandlerMetadataOnly);
        }

        if (await EnsureDocumentOpenAsync(binding.FilePath, addRecent: true, updateStatus: true, allowWorkspaceLoad: false)
            is not TextDocumentViewModel target || target.IsDisposed ||
            !string.Equals(target.Document.Text, binding.ExpectedText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(UiStrings.EventHandlerSourceChangedNavigation);
        }

        target.SetSelection(binding.NameOffset, binding.NameLength, binding.NameOffset);
        return true;
    }

    private async Task<DesignerEventBindingResult> ResolveDesignerEventAsync(
        DesignerDocumentViewModel document, MutableAstObjectNode node, string eventName, string handlerName,
        CancellationToken cancellationToken)
    {
        MutableAstDocument ast = document.SyncEngine.CurrentDocument
            ?? throw new InvalidOperationException(UiStrings.DesignerDocumentNotSynchronized);
        MutableAstObjectNode root = ast.Root
            ?? throw new InvalidOperationException(UiStrings.DesignerDocumentNoRoot);
        string className = GetDesignerClassName(root, ast)
            ?? throw new InvalidOperationException(UiStrings.EventHandlerClassRequired);
        TypeMetadata controlType = document.MetadataService?.GetType(node.XmlNamespace, node.TypeName)
            ?? throw new InvalidOperationException(UiStrings.EventHandlerControlTypeUnresolved);
        ProjectModel[] owners = _workspace?.Projects
            .Where(project => project.XamlFiles.Any(path => FileSystemPathComparison.Equals(path, document.FilePath)))
            .ToArray() ?? [];
        if (owners.Length > 1)
        {
            throw new InvalidOperationException(UiStrings.EventHandlerMultipleProjects);
        }

        IDesignerEventBindingService service = _languageRegistry?.Services.OfType<IDesignerEventBindingService>().FirstOrDefault()
            ?? throw new InvalidOperationException(UiStrings.EventHandlerServiceMissing);
        WorkspaceModel? workspace = _workspace;
        string originalXaml = document.SyncEngine.CurrentText
            ?? throw new InvalidOperationException(UiStrings.DesignerDocumentSynchronizing);
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
                throw new InvalidOperationException(UiStrings.EventHandlerSourceChangedValidation);
            }
        }

        EnsureDesignerEditContext(document, node, workspace, originalXaml, cancellationToken);
        foreach (var snapshot in openTexts)
        {
            TextDocumentViewModel? current = Documents.OfType<TextDocumentViewModel>()
                .FirstOrDefault(item => FileSystemPathComparison.Equals(item.FilePath, snapshot.Key));
            if (current is null || current.IsDisposed || !string.Equals(current.Document.Text, snapshot.Value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(UiStrings.EventHandlerCodeChanged);
            }
        }

        if (Documents.OfType<TextDocumentViewModel>().Any(item => item.IsModified &&
            string.Equals(Path.GetExtension(item.FilePath), ".cs", StringComparison.OrdinalIgnoreCase) && !openTexts.ContainsKey(item.FilePath)))
        {
            throw new InvalidOperationException(UiStrings.EventHandlerCodeEdited);
        }

        return result;
    }
}
