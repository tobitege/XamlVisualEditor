using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

/// <summary>Shows the panel of an extension tool.</summary>
/// <remarks>
/// The panel view is built once per extension view and set as control content. Content built by a data template
/// would be built again each time Dock shows the cached tool view again, which drops the scroll position, the
/// expanded nodes and every other state of the panel.
/// </remarks>
public sealed partial class ExtensionToolView : UserControl
{
    private readonly ContentControl _panelHost;
    private ExtensionTool? _tool;
    private ExtensionViewModel? _panelViewModel;

    public ExtensionToolView()
    {
        AvaloniaXamlLoader.Load(this);
        _panelHost = this.FindControl<ContentControl>("PanelHost")!;
        DataContextChanged += (_, _) => TrackTool(DataContext as ExtensionTool);
    }

    private void TrackTool(ExtensionTool? tool)
    {
        if (_tool is not null)
        {
            _tool.PropertyChanged -= OnToolPropertyChanged;
        }

        _tool = tool;
        if (_tool is not null)
        {
            _tool.PropertyChanged += OnToolPropertyChanged;
        }

        ShowPanel(_tool?.ExtensionViewModel);
    }

    private void OnToolPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExtensionTool.ExtensionViewModel))
        {
            ShowPanel(_tool?.ExtensionViewModel);
        }
    }

    private void ShowPanel(ExtensionViewModel? viewModel)
    {
        if (ReferenceEquals(viewModel, _panelViewModel) && _panelHost.Content is not null)
        {
            return;
        }

        _panelViewModel = viewModel;
        _panelHost.Content = viewModel switch
        {
            ExtensionTreeViewModel tree => new ExtensionTreeView { DataContext = tree },
            ExtensionWebviewViewModel webview => new ExtensionWebviewView { DataContext = webview },
            ExtensionCustomViewModel { ViewModel: { } custom } => BuildCustomView(custom),
            _ => null
        };
    }

    private static Control BuildCustomView(object viewModel)
    {
        Control view = new ViewLocator().Build(viewModel);
        view.DataContext = viewModel;
        return view;
    }
}
