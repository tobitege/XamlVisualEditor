using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Extensions.Hosting;

public sealed class ExtensionViewRegistry : IExtensionViewRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot<IExtensionTreeDataProvider>> _treeProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slot<IWebviewViewProvider>> _webviewProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Slot<ICustomViewProvider>> _customProviders = new(StringComparer.Ordinal);

    public event EventHandler<ExtensionViewRegistryChangedEventArgs>? Changed;

    public IDisposable RegisterTreeDataProvider<T>(string viewId, ITreeDataProvider<T> provider)
    {
        ValidateRegistration(viewId, provider);
        return Add<IExtensionTreeDataProvider>(_treeProviders, viewId, new TreeDataProviderAdapter<T>(provider));
    }

    public IDisposable RegisterWebviewViewProvider(string viewId, IWebviewViewProvider provider)
    {
        ValidateRegistration(viewId, provider);
        return Add(_webviewProviders, viewId, provider);
    }

    public IDisposable RegisterCustomViewProvider(string viewId, ICustomViewProvider provider)
    {
        ValidateRegistration(viewId, provider);
        return Add(_customProviders, viewId, provider);
    }

    public bool TryGetTreeProvider(string viewId, out IExtensionTreeDataProvider provider)
    {
        return TryGet(_treeProviders, viewId, out provider);
    }

    public bool TryGetWebviewProvider(string viewId, out IWebviewViewProvider provider)
    {
        return TryGet(_webviewProviders, viewId, out provider);
    }

    public bool TryGetCustomViewProvider(string viewId, out ICustomViewProvider provider)
    {
        return TryGet(_customProviders, viewId, out provider);
    }

    private static void ValidateRegistration(string viewId, object? provider)
    {
        if (string.IsNullOrWhiteSpace(viewId))
        {
            throw new ArgumentException("View id is required.", nameof(viewId));
        }

        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }
    }

    private IDisposable Add<TProvider>(Dictionary<string, Slot<TProvider>> target, string viewId, TProvider provider)
        where TProvider : class
    {
        Slot<TProvider> slot = new(provider);
        lock (_gate)
        {
            target[viewId] = slot;
        }

        Changed?.Invoke(this, new ExtensionViewRegistryChangedEventArgs(viewId));
        return new Registration(() => Remove(target, viewId, slot));
    }

    private bool TryGet<TProvider>(Dictionary<string, Slot<TProvider>> target, string viewId, out TProvider provider)
        where TProvider : class
    {
        lock (_gate)
        {
            if (target.TryGetValue(viewId, out Slot<TProvider>? slot))
            {
                provider = slot.Provider;
                return true;
            }
        }

        provider = null!;
        return false;
    }

    private void Remove<TProvider>(Dictionary<string, Slot<TProvider>> target, string viewId, Slot<TProvider> slot)
        where TProvider : class
    {
        lock (_gate)
        {
            if (!target.TryGetValue(viewId, out Slot<TProvider>? current) || !ReferenceEquals(current, slot))
            {
                return;
            }

            target.Remove(viewId);
        }

        Changed?.Invoke(this, new ExtensionViewRegistryChangedEventArgs(viewId));
    }

    /// <summary>Gives each registration its own identity, so a replaced registration cannot remove its successor.</summary>
    private sealed class Slot<TProvider>
    {
        public Slot(TProvider provider)
        {
            Provider = provider;
        }

        public TProvider Provider { get; }
    }

    private sealed class TreeDataProviderAdapter<T> : IExtensionTreeDataProvider
    {
        private readonly ITreeDataProvider<T> _provider;

        public TreeDataProviderAdapter(ITreeDataProvider<T> provider)
        {
            _provider = provider;
        }

        public event EventHandler? Changed
        {
            add => _provider.Changed += value;
            remove => _provider.Changed -= value;
        }

        public async Task<IReadOnlyList<object>> GetChildrenAsync(object? element, CancellationToken cancellationToken)
        {
            T? typed = element is null
                ? default
                : element is T match
                    ? match
                    : default;

            IReadOnlyList<T> children = await _provider.GetChildrenAsync(typed, cancellationToken).ConfigureAwait(false);
            if (children.Count == 0)
            {
                return Array.Empty<object>();
            }

            List<object> results = new(children.Count);
            foreach (T child in children)
            {
                results.Add(child!);
            }

            return results;
        }

        public Task<TreeItem> GetTreeItemAsync(object element, CancellationToken cancellationToken)
        {
            if (element is not T typed)
            {
                throw new InvalidOperationException("Tree element type mismatch for view provider.");
            }

            return _provider.GetTreeItemAsync(typed, cancellationToken);
        }
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

public sealed class NullExtensionTreeDataProvider : IExtensionTreeDataProvider
{
    public static NullExtensionTreeDataProvider Instance { get; } = new();

    private NullExtensionTreeDataProvider()
    {
    }

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task<IReadOnlyList<object>> GetChildrenAsync(object? element, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<object>>(Array.Empty<object>());
    }

    public Task<TreeItem> GetTreeItemAsync(object element, CancellationToken cancellationToken)
    {
        string label = element?.ToString() ?? string.Empty;
        return Task.FromResult(new TreeItem(label, null, null));
    }
}
