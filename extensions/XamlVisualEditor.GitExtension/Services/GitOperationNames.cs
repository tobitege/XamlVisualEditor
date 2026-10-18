using XamlVisualEditor.Core;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>Names the operation in progress for the status bar, the Git Changes panel and the confirmations.</summary>
internal static class GitOperationNames
{
    /// <summary>Returns the name of <paramref name="operation"/>, or null for <see cref="GitRepositoryOperation.None"/>.</summary>
    public static string? Get(GitRepositoryOperation operation) => operation switch
    {
        GitRepositoryOperation.Merging => GitStrings.OperationMerging,
        GitRepositoryOperation.Rebasing => GitStrings.OperationRebasing,
        GitRepositoryOperation.CherryPicking => GitStrings.OperationCherryPicking,
        GitRepositoryOperation.Reverting => GitStrings.OperationReverting,
        GitRepositoryOperation.ApplyingPatches => GitStrings.OperationApplyingPatches,
        _ => null
    };
}
