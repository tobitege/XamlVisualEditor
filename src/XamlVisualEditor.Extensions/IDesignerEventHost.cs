namespace XamlVisualEditor.Extensions;

public interface IDesignerEventHost
{
    Task<bool> SetEventHandlerAsync(
        string nodeId, string eventName, string? handlerName, CancellationToken cancellationToken);

    Task<bool> NavigateToEventHandlerAsync(
        string nodeId, string eventName, CancellationToken cancellationToken);
}
