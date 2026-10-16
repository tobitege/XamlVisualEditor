using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.TfvcExtension.ViewModels;

/// <summary>A folder or item node of the pending changes tree.</summary>
public sealed partial class PendingChangeNodeViewModel : ReactiveObject
{
    /// <summary>Creates a node.</summary>
    public PendingChangeNodeViewModel(string name, string fullPath, TfvcPendingChange? change, object? icon)
    {
        Name = name;
        FullPath = fullPath;
        Change = change;
        Icon = icon;
        ChangeLabel = change is null ? string.Empty : "[" + TfvcDisplayText.FormatChangeTypes(change.ChangeTypes) + "]";
        IsExpanded = true;
    }

    /// <summary>Gets the displayed name; collapsed folder chains contain several path segments.</summary>
    public string Name { get; }

    /// <summary>Gets the full local or server path of the node.</summary>
    public string FullPath { get; }

    /// <summary>Gets the pending change of an item node; null for pure folder nodes.</summary>
    public TfvcPendingChange? Change { get; }

    /// <summary>Gets the displayed change type.</summary>
    public string ChangeLabel { get; }

    /// <summary>Gets the node icon.</summary>
    public object? Icon { get; }

    /// <summary>Gets whether the node only groups other nodes.</summary>
    public bool IsFolder => Change is null || Change.IsFolder;

    /// <summary>Gets the child nodes.</summary>
    public ObservableCollection<PendingChangeNodeViewModel> Children { get; } = new();

    /// <summary>Gets or sets whether the node is expanded.</summary>
    [Reactive]
    public partial bool IsExpanded { get; set; }

    /// <summary>Collects the pending changes of this node and all descendants.</summary>
    public IReadOnlyList<TfvcPendingChange> GetChanges()
    {
        List<TfvcPendingChange> changes = new();
        CollectChanges(this, changes);
        return changes;
    }

    private static void CollectChanges(PendingChangeNodeViewModel node, List<TfvcPendingChange> changes)
    {
        if (node.Change is not null)
        {
            changes.Add(node.Change);
        }

        foreach (PendingChangeNodeViewModel child in node.Children)
        {
            CollectChanges(child, changes);
        }
    }
}

/// <summary>Builds the folder tree of pending changes like the Visual Studio pending changes view.</summary>
public static class PendingChangeTreeBuilder
{
    /// <summary>
    /// Groups pending changes by their local path (server path when unmapped). Folder chains without
    /// branches are collapsed into one node, so the root node shows the common folder of all changes.
    /// </summary>
    /// <param name="changes">Changes to group.</param>
    /// <param name="iconProvider">Returns the icon of a path; the flag is true for folders.</param>
    public static IReadOnlyList<PendingChangeNodeViewModel> Build(
        IEnumerable<TfvcPendingChange> changes,
        Func<string, bool, object?>? iconProvider = null)
    {
        TrieNode root = new(string.Empty, string.Empty, '\\');
        foreach (TfvcPendingChange change in changes)
        {
            bool isLocal = !string.IsNullOrWhiteSpace(change.LocalPath);
            string path = isLocal ? change.LocalPath! : change.ServerPath;
            char separator = isLocal ? '\\' : '/';
            string[] segments = path.Split(separator == '\\' ? ['\\', '/'] : ['/'], StringSplitOptions.RemoveEmptyEntries);
            TrieNode current = root;
            string currentPath = string.Empty;
            foreach (string segment in segments)
            {
                currentPath = currentPath.Length == 0 ? segment : currentPath + separator + segment;
                if (!current.Children.TryGetValue(segment, out TrieNode? child))
                {
                    child = new TrieNode(segment, currentPath, separator);
                    current.Children.Add(segment, child);
                }

                current = child;
            }

            current.Change = change;
        }

        List<PendingChangeNodeViewModel> roots = new();
        foreach (TrieNode child in SortChildren(root))
        {
            roots.Add(Convert(Collapse(child), iconProvider));
        }

        return roots;
    }

    private static TrieNode Collapse(TrieNode node)
    {
        TrieNode current = node;
        while (current.Change is null && current.Children.Count == 1)
        {
            TrieNode only = FirstChild(current);
            if (only.Children.Count == 0)
            {
                break;
            }

            TrieNode merged = new(current.Name + current.Separator + only.Name, only.FullPath, only.Separator)
            {
                Change = only.Change
            };
            foreach (KeyValuePair<string, TrieNode> grandChild in only.Children)
            {
                merged.Children.Add(grandChild.Key, grandChild.Value);
            }

            current = merged;
        }

        return current;
    }

    private static PendingChangeNodeViewModel Convert(TrieNode node, Func<string, bool, object?>? iconProvider)
    {
        bool isFolder = node.Children.Count > 0 || node.Change?.IsFolder == true;
        string displayName = node.Name.EndsWith(':') ? node.Name + node.Separator : node.Name;
        string fullPath = node.FullPath.EndsWith(':') ? node.FullPath + node.Separator : node.FullPath;
        PendingChangeNodeViewModel viewModel = new(displayName, fullPath, node.Change, iconProvider?.Invoke(fullPath, isFolder));
        foreach (TrieNode child in SortChildren(node))
        {
            viewModel.Children.Add(Convert(Collapse(child), iconProvider));
        }

        return viewModel;
    }

    private static List<TrieNode> SortChildren(TrieNode node)
    {
        List<TrieNode> children = new(node.Children.Values);
        children.Sort(static (left, right) =>
        {
            bool leftFolder = left.Children.Count > 0;
            bool rightFolder = right.Children.Count > 0;
            if (leftFolder != rightFolder)
            {
                return leftFolder ? -1 : 1;
            }

            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });
        return children;
    }

    private static TrieNode FirstChild(TrieNode node)
    {
        foreach (TrieNode child in node.Children.Values)
        {
            return child;
        }

        throw new InvalidOperationException("The node has no children.");
    }

    private sealed class TrieNode
    {
        public TrieNode(string name, string fullPath, char separator)
        {
            Name = name;
            FullPath = fullPath;
            Separator = separator;
        }

        public string Name { get; }

        public string FullPath { get; }

        public char Separator { get; }

        public TfvcPendingChange? Change { get; set; }

        public Dictionary<string, TrieNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
