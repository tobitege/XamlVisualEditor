using System.Collections.Generic;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>In-memory command registry.</summary>
public sealed class CommandRegistry : ICommands
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _handlers = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IDisposable Register(string commandId, Func<CommandContext, Task> handler)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new ArgumentException("Command id is required.", nameof(commandId));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        Entry entry = new(handler);
        lock (_gate)
        {
            _handlers[commandId] = entry;
        }

        return new Registration(() => Unregister(commandId, entry));
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(string commandId, IReadOnlyList<object?>? args, CancellationToken cancellationToken)
    {
        Entry? entry;
        lock (_gate)
        {
            _handlers.TryGetValue(commandId, out entry);
        }

        if (entry is null)
        {
            throw new InvalidOperationException("Command not found: " + commandId);
        }

        var context = new CommandContext(args, new NullWorkspace(), new NullWindow());
        await entry.Handler(context).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetCommandsAsync(CancellationToken cancellationToken)
    {
        List<string> commands = new();
        lock (_gate)
        {
            foreach (string key in _handlers.Keys)
            {
                commands.Add(key);
            }
        }

        return Task.FromResult<IReadOnlyList<string>>(commands);
    }

    private void Unregister(string commandId, Entry entry)
    {
        lock (_gate)
        {
            if (_handlers.TryGetValue(commandId, out Entry? current) && ReferenceEquals(current, entry))
            {
                _handlers.Remove(commandId);
            }
        }
    }

    /// <summary>Gives each registration its own identity, so a replaced registration cannot remove its successor.</summary>
    private sealed class Entry
    {
        public Entry(Func<CommandContext, Task> handler)
        {
            Handler = handler;
        }

        public Func<CommandContext, Task> Handler { get; }
    }

    private sealed class Registration : IDisposable
    {
        private readonly Action _dispose;
        private bool _isDisposed;

        public Registration(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _dispose();
            _isDisposed = true;
        }
    }
}
