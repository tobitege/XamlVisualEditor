namespace XamlVisualEditor.Extensions;

/// <summary>Edits structured property values without reducing them to scalar strings.</summary>
public interface IDesignerStructureHost
{
    Task<DesignerPropertyStructure> OpenPropertyStructureAsync(
        string nodeId, string propertyName, CancellationToken cancellationToken);

    Task ApplyPropertyStructureAsync(
        DesignerPropertyStructure original, string propertyElementXaml, CancellationToken cancellationToken);
}

/// <summary>A detached property draft and the document version from which it was obtained.</summary>
public sealed record DesignerPropertyStructure(
    string DocumentPath,
    string OwnerNodeId,
    string PropertyName,
    string DocumentText,
    string PropertyElementXaml,
    bool IsCollection,
    bool UsesImplicitContent,
    IReadOnlyList<DesignerStructureType> AvailableTypes)
{
    public DesignerGridAxis GridAxis { get; init; }
    public string? GridDefinitionContainerName { get; init; }
    public IReadOnlyList<int?>? GridDefinitionOriginalIndices { get; init; }
    public IReadOnlyList<string> GridDefinitionElementNames { get; init; } = Array.Empty<string>();
}

public enum DesignerGridAxis
{
    None,
    Rows,
    Columns
}

/// <summary>A constructible type offered by the loaded project metadata.</summary>
public sealed record DesignerStructureType(string TypeName, string XmlNamespace, string DisplayName)
{
    public DesignerGridAxis GridAxis { get; init; }
}
