using Avalonia;
using Avalonia.Controls;

namespace XamlVisualEditor.GitExtension.Views;

/// <summary>
/// Host view for the Git Repository panel. While it is attached to a window, it marks its view model as shown, so the
/// branches are read only for a visible panel.
/// </summary>
public sealed partial class GitRepositoryPanelView : UserControl
{
    private bool _isAttached;
    private GitRepositoryPanelViewModel? _shownViewModel;

    public GitRepositoryPanelView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        UpdateShownViewModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        UpdateShownViewModel();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateShownViewModel();
    }

    private void UpdateShownViewModel()
    {
        GitRepositoryPanelViewModel? next = _isAttached ? DataContext as GitRepositoryPanelViewModel : null;
        if (ReferenceEquals(next, _shownViewModel))
        {
            return;
        }

        if (_shownViewModel is not null)
        {
            _shownViewModel.IsShown = false;
        }

        _shownViewModel = next;
        if (next is not null)
        {
            next.IsShown = true;
        }
    }
}
