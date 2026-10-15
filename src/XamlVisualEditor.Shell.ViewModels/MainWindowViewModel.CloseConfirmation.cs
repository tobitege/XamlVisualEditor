using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Dock.Model.Core;
using Dock.Model.Core.Events;
using ReactiveUI;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>The answer to the question whether modified documents are saved before they close; the default keeps them open.</summary>
public enum SaveChangesChoice { Cancel, Save, Discard }

/// <summary>The answer to saving a file that changed on disk; the default saves nothing and keeps the editor text.</summary>
public enum SaveConflictChoice { Cancel, Overwrite, Reload }

public sealed partial class MainWindowViewModel
{
    private bool _isClosingDocument;
    private readonly HashSet<IEditorDocumentViewModel> _confirmedDockCloses = new();

    /// <summary>Asks which of the listed modified files are saved before they close.</summary>
    public Interaction<IReadOnlyList<string>, SaveChangesChoice> SaveChangesInteraction { get; } = new();

    /// <summary>
    /// Asks whether the named file, which changed on disk after the editor read it, is overwritten with the editor text
    /// or reloaded from disk.
    /// </summary>
    public Interaction<string, SaveConflictChoice> SaveConflictInteraction { get; } = new();

    /// <summary>Gets whether the user already confirmed closing the application.</summary>
    public bool IsShutdownConfirmed { get; private set; }

    /// <summary>
    /// Saves or discards the modified documents before they close. Manual saving asks the user, automatic saving saves
    /// without asking, and disabled saving discards. An automatic save that fails asks the user as well. Returns false
    /// when the user cancels or a save the user chose fails.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync(IEnumerable<IEditorDocumentViewModel> documents)
    {
        IEditorDocumentViewModel[] modified = documents.Where(document => document.IsModified).ToArray();
        if (modified.Length == 0 || SaveBehavior == SaveBehavior.NoSaving)
        {
            return true;
        }

        if (SaveBehavior == SaveBehavior.AutoSave)
        {
            if (await TrySaveBeforeCloseAsync(modified))
            {
                return true;
            }

            // A write-protected file or a declined conflict must not keep the documents open for good:
            // the question lets the user discard the changes.
            modified = modified.Where(document => document.IsModified).ToArray();
            if (modified.Length == 0)
            {
                return true;
            }
        }

        SaveChangesChoice choice = await SaveChangesInteraction.Handle(modified.Select(document => document.FileName).ToArray());
        if (choice != SaveChangesChoice.Save)
        {
            return choice == SaveChangesChoice.Discard;
        }

        return await TrySaveBeforeCloseAsync(modified);
    }

    private async Task<bool> TrySaveBeforeCloseAsync(IEnumerable<IEditorDocumentViewModel> documents)
    {
        foreach (IEditorDocumentViewModel document in documents)
        {
            bool saved = !HasWorkspace && IsUntitledDocument(document)
                ? await TrySaveUntitledDocumentAsync(document)
                : await TrySaveDocumentAsync(document);
            // A document reloaded from disk instead of saved has no changes left to lose.
            if (!saved && document.IsModified)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Confirms the modified documents before the application closes.</summary>
    public async Task<bool> ConfirmShutdownAsync()
    {
        IsShutdownConfirmed = await ConfirmCloseAsync(Documents.ToArray());
        return IsShutdownConfirmed;
    }

    /// <summary>Opens the most recent workspace or folder from the recent list.</summary>
    public async Task<bool> RestoreLastWorkspaceAsync()
    {
        RecentFileEntry? entry = RecentFiles.FirstOrDefault(item => item.Kind is RecentEntryKind.Workspace or RecentEntryKind.Folder);
        if (entry is null)
        {
            return false;
        }

        await OpenRecentAsync(entry);
        return true;
    }

    private async Task ConfirmAndCloseDocumentAsync(IEditorDocumentViewModel document)
    {
        if (await ConfirmCloseAsync([document]) && Documents.Contains(document))
        {
            CloseDocument(document);
        }
    }

    // Dock closes its tab before it reports the close, so a modified document is kept open until the user answers.
    private void OnDockableClosing(object? sender, DockableClosingEventArgs e)
    {
        IEditorDocumentViewModel? document = e.Dockable switch
        {
            DesignerDocument designer => designer.DocumentViewModel,
            TextDocument text => text.DocumentViewModel,
            _ => null
        };
        if (document is null || _isClosingDocument || _confirmedDockCloses.Remove(document) || !document.IsModified)
        {
            return;
        }

        e.Cancel = true;
        _ = ReportFailureAsync(ConfirmDockCloseAsync(document, e.Dockable!));
    }

    private async Task ConfirmDockCloseAsync(IEditorDocumentViewModel document, IDockable dockable)
    {
        if (!await ConfirmCloseAsync([document]) || !Documents.Contains(document))
        {
            return;
        }

        _confirmedDockCloses.Add(document);
        DockFactory.CloseDockable(dockable);
    }
}
