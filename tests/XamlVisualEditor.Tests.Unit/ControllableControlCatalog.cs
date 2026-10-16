using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.Tests.Unit;

/// <summary>Control catalog whose requests finish only when a test completes them, in any order.</summary>
internal sealed class ControllableControlCatalog : IControlCatalog
{
    private readonly object _gate = new();
    private readonly List<TaskCompletionSource<Request>> _requests = new();
    private int _requestCount;

    /// <inheritdoc />
    public event EventHandler? Changed { add { } remove { } }

    /// <inheritdoc />
    public bool HasProjectAssemblies { get; set; }

    /// <inheritdoc />
    public Task<ControlCatalogSnapshot> GetControlsAsync(CancellationToken cancellationToken,
        IProgress<ControlCatalogProgress>? progress = null, bool includeProjectControls = true, bool forceRefresh = false)
    {
        Request request = new(includeProjectControls);
        TaskCompletionSource<Request> slot;
        lock (_gate) slot = GetSlot(_requestCount++);
        slot.SetResult(request);
        return request.Result;
    }

    /// <summary>Returns the request with the given zero-based position once the view model has sent it.</summary>
    public Task<Request> WaitForRequestAsync(int index)
    {
        lock (_gate) return GetSlot(index).Task;
    }

    /// <summary>Creates a project control of the library "Project.Controls".</summary>
    public static ControlCatalogItem ProjectControl(string name) =>
        new(name, "Project.Controls." + name, "using:Project.Controls", "Project.Controls", name, "Controls", false);

    /// <summary>Creates a standard Avalonia control.</summary>
    public static ControlCatalogItem StandardControl(string name) =>
        new(name, "Avalonia.Controls." + name, "https://github.com/avaloniaui", "Avalonia.Controls", name, "Controls", true);

    private TaskCompletionSource<Request> GetSlot(int index)
    {
        while (_requests.Count <= index)
            _requests.Add(new TaskCompletionSource<Request>(TaskCreationOptions.RunContinuationsAsynchronously));
        return _requests[index];
    }

    /// <summary>One catalog request; it ignores cancellation like a reader that is already past its last check.</summary>
    internal sealed class Request
    {
        private readonly TaskCompletionSource<ControlCatalogSnapshot> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Request(bool includeProjectControls) => IncludeProjectControls = includeProjectControls;

        public bool IncludeProjectControls { get; }

        public Task<ControlCatalogSnapshot> Result => _result.Task;

        public void Complete(params ControlCatalogItem[] controls) =>
            _result.SetResult(new ControlCatalogSnapshot(controls, Array.Empty<string>()));
    }
}
