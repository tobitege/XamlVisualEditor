using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class ShellDesignerHost : IDesignerEventHost
{
    public async Task<bool> SetEventHandlerAsync(
        string nodeId, string eventName, string? handlerName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(
                () => SetEventHandlerAsync(nodeId, eventName, handlerName, cancellationToken), DispatcherPriority.Background);
        }

        var (document, node) = GetEventContext(nodeId, eventName);
        return await _mainViewModel.SetDesignerEventHandlerAsync(document, node, eventName, handlerName, cancellationToken);
    }

    public async Task<bool> NavigateToEventHandlerAsync(
        string nodeId, string eventName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(
                () => NavigateToEventHandlerAsync(nodeId, eventName, cancellationToken), DispatcherPriority.Background);
        }

        var (document, node) = GetEventContext(nodeId, eventName);
        return await _mainViewModel.NavigateToDesignerEventHandlerAsync(document, node, eventName, cancellationToken);
    }

    private (DesignerDocumentViewModel Document, MutableAstObjectNode Node) GetEventContext(string nodeId, string eventName)
    {
        DesignerDocumentViewModel? document = GetActiveDocument();
        if (document is null || document.IsDisposed || !document.SyncEngine.CanEditAst ||
            !Guid.TryParse(nodeId, out Guid id) || document.NodeMap.FindById(id) is not MutableAstObjectNode node)
        {
            throw new InvalidOperationException(UiStrings.DesignerSelectSynchronizedControl);
        }

        if (!GetEventsCore(nodeId).Any(item => string.Equals(item.Name, eventName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(string.Format(UiStrings.EventHandlerEventNotExposed, eventName));
        }

        return (document, node);
    }
}
