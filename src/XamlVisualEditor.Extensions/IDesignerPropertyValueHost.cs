namespace XamlVisualEditor.Extensions;

/// <summary>Distinguishes explicit null assignments from removing a local property value.</summary>
public interface IDesignerPropertyValueHost
{
    /// <summary>Sets a local null value and returns its namespace-qualified XAML expression.</summary>
    Task<string> SetPropertyNullAsync(string nodeId, string propertyName, CancellationToken cancellationToken);
}
