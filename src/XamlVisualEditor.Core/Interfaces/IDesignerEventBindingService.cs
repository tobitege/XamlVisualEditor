namespace XamlVisualEditor.Core.Interfaces;

public interface IDesignerEventBindingService
{
    Task<DesignerEventBindingResult> ResolveEventHandlerAsync(
        DesignerEventBindingRequest request, CancellationToken cancellationToken = default);
}

public sealed record DesignerEventBindingRequest(
    string? ProjectPath,
    string ClassName,
    string ControlTypeFullName,
    string? ControlAssemblyName,
    string EventName,
    string HandlerName,
    IReadOnlyDictionary<string, string> OpenDocumentTexts);

/// <summary>A valid event binding, with an editable definition when source is available.</summary>
public sealed record DesignerEventBindingResult(
    string? FilePath, string? ExpectedText, int NameOffset, int NameLength);
