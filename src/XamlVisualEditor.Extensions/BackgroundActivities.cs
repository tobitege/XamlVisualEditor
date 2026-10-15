namespace XamlVisualEditor.Extensions;

/// <summary>Announces host or extension work that keeps the editor from being fully ready.</summary>
public interface IBackgroundActivities
{
    /// <summary>Starts an activity whose description the status bar shows while it runs.</summary>
    /// <param name="description">A short, localized description, for example "Indexing workspace...".</param>
    /// <returns>A handle that ends the activity when disposed; disposing it again has no effect.</returns>
    IDisposable Begin(string description);
}
