using System;

namespace XamlVisualEditor.Core;

/// <summary>What <see cref="Interfaces.IGitPatchService.ApplySelectionAsync"/> does with the selected lines.</summary>
public enum GitPatchAction
{
    /// <summary>Copies the selected changes of the working file into the index.</summary>
    Stage,

    /// <summary>Takes the selected staged changes back out of the index; the working file stays unchanged.</summary>
    Unstage,

    /// <summary>Reverts the selected changes in the working file; the index stays unchanged.</summary>
    Discard
}

/// <summary>Lines of a shown diff of one file that a patch action works on.</summary>
/// <param name="Path">The repository-relative path of the file, with <c>/</c> as separator.</param>
/// <param name="Action">The action; <see cref="GitPatchAction.Unstage"/> works on the staged diff, the others on the working tree diff.</param>
/// <param name="ShownDiff">The diff text the lines were selected in, as <see cref="GitDiff.RawText"/> holds it.</param>
/// <param name="FirstLine">The 0-based index of the first selected line of <paramref name="ShownDiff"/>.</param>
/// <param name="LineCount">The number of selected lines.</param>
public sealed record GitDiffSelection(string Path, GitPatchAction Action, string ShownDiff, int FirstLine, int LineCount);

/// <summary>Why a patch of selected diff lines was not applied.</summary>
public enum GitPatchRejection
{
    /// <summary>The diff of the file is no longer the shown one.</summary>
    DiffChanged,

    /// <summary>The selected lines contain no added or removed line.</summary>
    NothingSelected,

    /// <summary>The diff has more than one file header, for example for a change of the file type.</summary>
    MultipleFiles,

    /// <summary>
    /// The selection would keep the last line of a file without line break in front of other lines, which git would
    /// join into one line.
    /// </summary>
    MissingNewline
}

/// <summary>Thrown when a patch of selected diff lines is not applied; the index and the working file are unchanged.</summary>
public sealed class GitPatchRejectedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public GitPatchRejectedException(GitPatchRejection reason)
        : base("The selected diff lines were not applied: " + reason + ".")
    {
        Reason = reason;
    }

    /// <summary>Gets why the patch was not applied.</summary>
    public GitPatchRejection Reason { get; }
}
