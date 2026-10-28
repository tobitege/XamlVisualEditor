namespace XamlVisualEditor.Core.Interfaces;

/// <summary>Calculates C# changes for a control or event handler declared by an AXAML document.</summary>
public interface IDesignerCodeBehindRefactoringService
{
    /// <summary>
    /// Returns a detached edit plan for the member <see cref="DesignerCodeBehindRenameRequest.Member"/> names;
    /// does not apply changes to documents or the workspace.
    /// </summary>
    Task<LanguageWorkspaceEdit?> PrepareControlRenameAsync(DesignerCodeBehindRenameRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Identifies the owning project and class, including current unsaved C# document text. Without a project path the
/// project that lists <see cref="XamlFilePath"/> among its additional files owns the class.
/// </summary>
public sealed record DesignerCodeBehindRenameRequest(
    string? ProjectPath,
    string ClassName,
    string OldName,
    string? NewName,
    IReadOnlyDictionary<string, string> OpenDocumentTexts)
{
    /// <summary>Gets the kind of member that is renamed; a control by default.</summary>
    public DesignerCodeBehindMember Member { get; init; }

    /// <summary>Gets the AXAML file that declares the member, which identifies the project without a project path.</summary>
    public string? XamlFilePath { get; init; }
}

/// <summary>The kind of code-behind member a rename refers to.</summary>
public enum DesignerCodeBehindMember
{
    /// <summary>The field or property generated for a named control, and the lookups of the control by name.</summary>
    Control,

    /// <summary>The event handler methods of that name.</summary>
    EventHandler
}
