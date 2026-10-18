using Avalonia.Controls;

namespace XamlVisualEditor.GitExtension.Views;

/// <summary>Document that shows the commit of every line of a saved file.</summary>
public sealed partial class GitBlameDocumentView : UserControl
{
    public GitBlameDocumentView()
    {
        InitializeComponent();
    }
}
