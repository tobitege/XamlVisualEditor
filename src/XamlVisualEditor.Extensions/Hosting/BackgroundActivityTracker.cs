namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>Starts activities and reads the ones that are currently running.</summary>
public interface IBackgroundActivityMonitor : IBackgroundActivities
{
    /// <summary>Gets the description of the most recently started activity that still runs, or null when none runs.</summary>
    string? CurrentDescription { get; }

    /// <summary>Raised on the thread that started or ended an activity.</summary>
    event EventHandler? Changed;
}

/// <summary>Thread-safe registry of running background activities.</summary>
public sealed class BackgroundActivityTracker : IBackgroundActivityMonitor
{
    private readonly object _gate = new();
    private readonly List<Activity> _running = new();

    /// <inheritdoc />
    public string? CurrentDescription
    {
        get
        {
            lock (_gate)
            {
                return _running.Count == 0 ? null : _running[^1].Description;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IDisposable Begin(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Activity activity = new(this, description);
        lock (_gate)
        {
            _running.Add(activity);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return activity;
    }

    private void End(Activity activity)
    {
        bool removed;
        lock (_gate)
        {
            removed = _running.Remove(activity);
        }

        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Activity(BackgroundActivityTracker owner, string description) : IDisposable
    {
        public string Description { get; } = description;

        public void Dispose() => owner.End(this);
    }
}
