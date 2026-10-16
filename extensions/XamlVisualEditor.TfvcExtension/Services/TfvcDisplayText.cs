using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>Builds localized display texts for TFVC states, change types and errors.</summary>
public static class TfvcDisplayText
{
    private const int MAX_ERROR_LINES = 6;

    /// <summary>Formats the change types of a pending change, for example "edit, encoding".</summary>
    public static string FormatChangeTypes(IReadOnlyList<string> changeTypes)
    {
        StringBuilder builder = new();
        foreach (string changeType in changeTypes)
        {
            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            builder.Append(FormatChangeType(changeType));
        }

        return builder.ToString();
    }

    /// <summary>Formats one change type.</summary>
    public static string FormatChangeType(string changeType)
    {
        return changeType.ToLowerInvariant() switch
        {
            "edit" => TfvcStrings.ChangeEdit,
            "add" => TfvcStrings.ChangeAdd,
            "delete" => TfvcStrings.ChangeDelete,
            "rename" => TfvcStrings.ChangeRename,
            "undelete" => TfvcStrings.ChangeUndelete,
            "branch" => TfvcStrings.ChangeBranch,
            "merge" => TfvcStrings.ChangeMerge,
            "encoding" => TfvcStrings.ChangeEncoding,
            "lock" => TfvcStrings.ChangeLock,
            "rollback" => TfvcStrings.ChangeRollback,
            "property" => TfvcStrings.ChangeProperty,
            "sourcerename" => TfvcStrings.ChangeSourceRename,
            _ => changeType
        };
    }

    /// <summary>Formats a file state for the status bar.</summary>
    public static string FormatFileState(TfvcFileState state, bool isChecking)
    {
        string text = isChecking
            ? TfvcStrings.FileStateChecking
            : state.Kind switch
            {
                TfvcFileStateKind.NotInWorkspace => TfvcStrings.FileStateNotInWorkspace,
                TfvcFileStateKind.NotVersioned => TfvcStrings.FileStateNotVersioned,
                TfvcFileStateKind.CheckedIn => TfvcStrings.FileStateCheckedIn,
                TfvcFileStateKind.Offline => TfvcStrings.FileStateOffline,
                TfvcFileStateKind.Pending => string.Format(
                    CultureInfo.CurrentCulture,
                    TfvcStrings.FileStatePendingFormat,
                    state.PendingChange is null ? string.Empty : FormatChangeTypes(state.PendingChange.ChangeTypes)),
                _ => TfvcStrings.FileStateUnknown
            };
        return string.Format(CultureInfo.CurrentCulture, TfvcStrings.StatusBarFormat, text);
    }

    /// <summary>Describes an exception of a TFVC operation for the user.</summary>
    public static string DescribeError(Exception exception)
    {
        return exception switch
        {
            TfExeNotFoundException => TfvcStrings.TfExeNotFound,
            TfCommandLineTooLongException => TfvcStrings.CommandLineTooLong,
            TfvcWorkspaceFolderNotFoundException folder => string.Format(
                CultureInfo.CurrentCulture,
                TfvcStrings.WorkspaceFolderMissingFormat,
                folder.WorkspaceName),
            TfCommandException command => string.Format(
                CultureInfo.CurrentCulture,
                TfvcStrings.CommandFailedFormat,
                command.Command,
                LimitLines(command.Result.CombinedOutput)),
            TfvcServerException server => string.Format(CultureInfo.CurrentCulture, TfvcStrings.ServerRequestFailedFormat, server.Message),
            _ => exception.Message
        };
    }

    private static string LimitLines(string text)
    {
        string[] lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length <= MAX_ERROR_LINES)
        {
            return string.Join(Environment.NewLine, lines);
        }

        return string.Join(Environment.NewLine, lines, 0, MAX_ERROR_LINES) + Environment.NewLine + "...";
    }
}
