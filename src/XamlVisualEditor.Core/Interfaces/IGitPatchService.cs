using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Core.Interfaces;

/// <summary>
/// Stages, unstages and discards selected lines of a file diff and amends the last commit. Both methods throw
/// <see cref="System.InvalidOperationException"/> with git's error output when git fails.
/// </summary>
public interface IGitPatchService
{
    /// <summary>
    /// Applies the selected lines of a shown diff to the index or the working file. The diff is read again first; when
    /// it differs from <see cref="GitDiffSelection.ShownDiff"/>, nothing is applied.
    /// </summary>
    /// <exception cref="GitPatchRejectedException">The diff changed, the selection holds no change, or the selected
    /// lines cannot form a valid patch; the index and the working file are unchanged.</exception>
    Task ApplySelectionAsync(string repoPath, GitDiffSelection selection, CancellationToken ct = default);

    /// <summary>Replaces the last commit by one with the staged changes added and <paramref name="message"/> as message.</summary>
    Task AmendAsync(string repoPath, string message, CancellationToken ct = default);
}
