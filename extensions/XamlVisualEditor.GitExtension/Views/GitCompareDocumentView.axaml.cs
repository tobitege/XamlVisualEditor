using Avalonia.Controls;

namespace XamlVisualEditor.GitExtension.Views;

/// <summary>Document that compares a file with its version in HEAD or in the index.</summary>
public sealed partial class GitCompareDocumentView : UserControl
{
    public GitCompareDocumentView()
    {
        InitializeComponent();
    }
}
