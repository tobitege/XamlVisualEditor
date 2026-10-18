using Avalonia;
using Avalonia.Controls;

namespace XamlVisualEditor.GitExtension.Views;

/// <summary>
/// Host view for the Git History panel. While it is attached to a window, it marks its view model as shown, so the
/// commits are read only for a visible panel.
/// </summary>
public sealed partial class GitHistoryView : UserControl
{
    private bool _isAttached;
    private GitHistoryViewModel? _shownViewModel;

    public GitHistoryView()
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
        GitHistoryViewModel? next = _isAttached ? DataContext as GitHistoryViewModel : null;
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
