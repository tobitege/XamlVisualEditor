using Microsoft.CodeAnalysis.CSharp;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Language;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Renames document-local declarations and their AXAML bindings in one undo transaction.</summary>
internal static class DesignerNameEditor
{
    public static void RenameWithCodeBehind(
        DesignerDocumentViewModel document,
        MutableAstObjectNode node,
        string? value,
        System.Collections.Generic.IReadOnlyList<XamlVisualEditor.Sync.DocumentCompanionChange> companionChanges)
    {
        using var transaction = document.SyncEngine.BeginEditTransaction("Rename control and code-behind");
        foreach (XamlVisualEditor.Sync.DocumentCompanionChange change in companionChanges)
        {
            transaction.Enlist(change);
        }

        Rename(document, node, value);
        transaction.Commit();
    }

    internal static void Rename(DesignerDocumentViewModel document, MutableAstObjectNode node, string? value)
    {
        if (!string.IsNullOrEmpty(value) && (value.StartsWith('@') || !SyntaxFacts.IsValidIdentifier(value)))
            throw new InvalidOperationException(UiStrings.DesignerNameInvalid);
        string? newName = string.IsNullOrEmpty(value) ? null : value;
        if (XamlNames.PlanRename(document.SyncEngine.CurrentDocument!, node, newName, document.MetadataService) is not { } rename) return;
        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Rename control", SyncSource.PropertyEditor);
        rename();
        edit.Commit();
    }
}
