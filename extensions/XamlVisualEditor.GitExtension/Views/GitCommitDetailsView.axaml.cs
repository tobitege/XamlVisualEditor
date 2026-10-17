using System;
using Avalonia;
using Avalonia.Controls;

namespace XamlVisualEditor.GitExtension.Views;

/// <summary>Details of the commit selected in the Git History panel: header, message, files and the diff of a file.</summary>
public sealed partial class GitCommitDetailsView : UserControl
{
    public GitCommitDetailsView()
    {
        InitializeComponent();
        // The summary holds the abbreviated id, so it changes with every other commit, whose header starts at the top.
        CommitSummary.GetObservable(TextBlock.TextProperty).Subscribe(_ => HeaderScroller.Offset = default);
    }
}
