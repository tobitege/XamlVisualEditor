namespace XamlVisualEditor.Core.Interfaces;

/// <summary>Calculates C# changes for a control declaration owned by a known project.</summary>
public interface IDesignerCodeBehindRefactoringService
{
    /// <summary>Returns a detached edit plan; does not apply changes to documents or the workspace.</summary>
    Task<LanguageWorkspaceEdit?> PrepareControlRenameAsync(DesignerCodeBehindRenameRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Identifies the owning project and class, including current unsaved C# document text.</summary>
public sealed record DesignerCodeBehindRenameRequest(
    string ProjectPath,
    string ClassName,
    string OldName,
    string? NewName,
    IReadOnlyDictionary<string, string> OpenDocumentTexts);
