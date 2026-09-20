using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Adapts diagnostics from the shell to extension diagnostics service.</summary>
public sealed class DiagnosticsServiceAdapter : IDiagnosticsService, IDisposable
{
    private static readonly IReadOnlyList<DiagnosticsChannelInfo> EmptyChannels = Array.Empty<DiagnosticsChannelInfo>();
    private readonly MainWindowViewModel _mainViewModel;
    private IDisposable? _activeDocumentSubscription;
    private IDisposable? _diagnosticsSubscription;
    private IDisposable? _documentsSubscription;
    private readonly Dictionary<IEditorDocumentViewModel, IDisposable> _documentDiagnosticsSubscriptions = new();
    private IEditorDocumentViewModel? _activeDocument;
    private IReadOnlyList<DiagnosticsChannelInfo> _lastChannels = EmptyChannels;

    public DiagnosticsServiceAdapter(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        _activeDocumentSubscription = _mainViewModel
            .WhenAnyValue(vm => vm.ActiveDocument)
            .Subscribe(UpdateActiveDocument);

        _documentsSubscription = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => _mainViewModel.Documents.CollectionChanged += h,
                h => _mainViewModel.Documents.CollectionChanged -= h)
            .Subscribe(_ =>
            {
                SyncDocumentSubscriptions();
                PublishChannelDiagnostics(null, Array.Empty<LanguageDiagnostic>());
                PublishSnapshotDiagnostics();
            });

        SyncDocumentSubscriptions();
        PublishSnapshotDiagnostics();
    }

    public event EventHandler<DiagnosticsPublishedEventArgs>? DiagnosticsPublished;

    public event EventHandler<DiagnosticsChannelsChangedEventArgs>? ChannelsChanged;

    public event EventHandler<DiagnosticsChannelPublishedEventArgs>? DiagnosticsChannelPublished;

    public event EventHandler<DiagnosticsSnapshotPublishedEventArgs>? DiagnosticsSnapshotPublished;

    public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

    public async Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(string? filePath, CancellationToken ct)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return GetDiagnosticsCore(filePath, null);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => GetDiagnosticsCore(filePath, null),
            DispatcherPriority.Background,
            ct);
    }

    public async Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DiagnosticsQuery query, CancellationToken ct)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return GetDiagnosticsCore(query.FilePath, query.ChannelId);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => GetDiagnosticsCore(query.FilePath, query.ChannelId),
            DispatcherPriority.Background,
            ct);
    }

    public async Task<IReadOnlyList<DiagnosticsChannelInfo>> GetChannelsAsync(CancellationToken ct)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return GetChannelsCore();
        }

        return await Dispatcher.UIThread.InvokeAsync(
            GetChannelsCore,
            DispatcherPriority.Background,
            ct);
    }

    public async Task<IReadOnlyList<DiagnosticsDocumentSnapshot>> GetDiagnosticsSnapshotAsync(
        DiagnosticsQuery query,
        CancellationToken ct)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return GetDiagnosticsSnapshotCore(query);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => GetDiagnosticsSnapshotCore(query),
            DispatcherPriority.Background,
            ct);
    }

    private IReadOnlyList<LanguageDiagnostic> GetDiagnosticsCore(string? filePath, string? channelId)
    {
        IEditorDocumentViewModel? document = string.IsNullOrWhiteSpace(filePath)
            ? _activeDocument ?? _mainViewModel.ActiveDocument : FindDocument(filePath);
        if (document is null)
        {
            return Array.Empty<LanguageDiagnostic>();
        }

        if (string.IsNullOrWhiteSpace(filePath) || string.Equals(document.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            List<LanguageDiagnostic> diagnostics = GetDocumentDiagnostics(document).ToList();
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return diagnostics;
            }

            List<LanguageDiagnostic> filtered = new();
            foreach (LanguageDiagnostic diagnostic in diagnostics)
            {
                if (string.Equals(GetChannelId(diagnostic.Source), channelId, StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(diagnostic);
                }
            }

            return filtered;
        }

        return Array.Empty<LanguageDiagnostic>();
    }

    private IReadOnlyList<DiagnosticsChannelInfo> GetChannelsCore()
    {
        IReadOnlyList<LanguageDiagnostic> diagnostics = GetAllDiagnostics();
        return BuildChannels(diagnostics);
    }

    public void Dispose()
    {
        _diagnosticsSubscription?.Dispose();
        _activeDocumentSubscription?.Dispose();
        _documentsSubscription?.Dispose();
        _diagnosticsSubscription = null;
        _activeDocumentSubscription = null;
        _documentsSubscription = null;

        foreach (IDisposable subscription in _documentDiagnosticsSubscriptions.Values)
        {
            subscription.Dispose();
        }

        _documentDiagnosticsSubscriptions.Clear();
    }

    private void UpdateActiveDocument(IEditorDocumentViewModel? document)
    {
        _diagnosticsSubscription?.Dispose();
        _diagnosticsSubscription = null;
        _activeDocument = document;

        if (document is null)
        {
            DiagnosticsChanged?.Invoke(this, new DiagnosticsChangedEventArgs(null));
            DiagnosticsPublished?.Invoke(this, new DiagnosticsPublishedEventArgs(null, Array.Empty<LanguageDiagnostic>()));
            PublishChannelDiagnostics(null, Array.Empty<LanguageDiagnostic>());
            PublishSnapshotDiagnostics();
            return;
        }

        _diagnosticsSubscription = ObserveDocumentDiagnostics(document)
            .Subscribe(_ =>
            {
                IReadOnlyList<LanguageDiagnostic> diagnostics = GetDocumentDiagnostics(document).ToArray();
                DiagnosticsChanged?.Invoke(this, new DiagnosticsChangedEventArgs(document.FilePath));
                DiagnosticsPublished?.Invoke(this, new DiagnosticsPublishedEventArgs(document.FilePath, diagnostics));
                PublishChannelDiagnostics(document.FilePath, diagnostics);
                PublishSnapshotDiagnostics();
            });

        IReadOnlyList<LanguageDiagnostic> initialDiagnostics = GetDocumentDiagnostics(document).ToArray();
        DiagnosticsChanged?.Invoke(this, new DiagnosticsChangedEventArgs(document.FilePath));
        DiagnosticsPublished?.Invoke(this, new DiagnosticsPublishedEventArgs(document.FilePath, initialDiagnostics));
        PublishChannelDiagnostics(document.FilePath, initialDiagnostics);
        PublishSnapshotDiagnostics();
    }

    private void PublishChannelDiagnostics(string? filePath, IReadOnlyList<LanguageDiagnostic> diagnostics)
    {
        IReadOnlyList<DiagnosticsChannelInfo> channels = BuildChannels(GetAllDiagnostics());
        if (!ChannelsEqual(_lastChannels, channels))
        {
            _lastChannels = channels;
            ChannelsChanged?.Invoke(this, new DiagnosticsChannelsChangedEventArgs(channels));
        }

        if (channels.Count == 0)
        {
            return;
        }

        Dictionary<string, List<LanguageDiagnostic>> grouped = new(StringComparer.OrdinalIgnoreCase);
        foreach (LanguageDiagnostic diagnostic in diagnostics)
        {
            string channelId = GetChannelId(diagnostic.Source);
            if (!grouped.TryGetValue(channelId, out List<LanguageDiagnostic>? list))
            {
                list = new List<LanguageDiagnostic>();
                grouped[channelId] = list;
            }

            list.Add(diagnostic);
        }

        foreach (DiagnosticsChannelInfo channel in channels)
        {
            grouped.TryGetValue(channel.Id, out List<LanguageDiagnostic>? list);
            DiagnosticsChannelPublished?.Invoke(this, new DiagnosticsChannelPublishedEventArgs(
                channel.Id,
                filePath,
                list ?? (IReadOnlyList<LanguageDiagnostic>)Array.Empty<LanguageDiagnostic>()));
        }
    }

    private void PublishSnapshotDiagnostics()
    {
        IReadOnlyList<DiagnosticsDocumentSnapshot> snapshots = GetDiagnosticsSnapshotCore(new DiagnosticsQuery(null, null));
        DiagnosticsSnapshotPublished?.Invoke(this, new DiagnosticsSnapshotPublishedEventArgs(snapshots));
    }

    private IReadOnlyList<DiagnosticsDocumentSnapshot> GetDiagnosticsSnapshotCore(DiagnosticsQuery query)
    {
        List<DiagnosticsDocumentSnapshot> snapshots = new();
        if (!string.IsNullOrWhiteSpace(query.FilePath))
        {
            IEditorDocumentViewModel? doc = FindDocument(query.FilePath);
            if (doc is null)
            {
                return Array.Empty<DiagnosticsDocumentSnapshot>();
            }

            IReadOnlyList<LanguageDiagnostic> diagnostics = FilterDiagnostics(GetDocumentDiagnostics(doc), query.ChannelId);
            if (diagnostics.Count == 0)
            {
                return Array.Empty<DiagnosticsDocumentSnapshot>();
            }

            snapshots.Add(new DiagnosticsDocumentSnapshot(doc.FilePath, diagnostics));
            return snapshots;
        }

        foreach (IEditorDocumentViewModel doc in GetDocuments())
        {
            IReadOnlyList<LanguageDiagnostic> diagnostics = FilterDiagnostics(GetDocumentDiagnostics(doc), query.ChannelId);
            if (diagnostics.Count == 0)
            {
                continue;
            }

            snapshots.Add(new DiagnosticsDocumentSnapshot(doc.FilePath, diagnostics));
        }

        return snapshots;
    }

    private IReadOnlyList<LanguageDiagnostic> GetAllDiagnostics()
    {
        List<LanguageDiagnostic> all = new();
        foreach (IEditorDocumentViewModel doc in GetDocuments())
        {
            all.AddRange(GetDocumentDiagnostics(doc));
        }

        return all;
    }

    private IReadOnlyList<LanguageDiagnostic> FilterDiagnostics(
        IReadOnlyList<LanguageDiagnostic> diagnostics,
        string? channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return diagnostics;
        }

        List<LanguageDiagnostic> filtered = new();
        foreach (LanguageDiagnostic diagnostic in diagnostics)
        {
            if (string.Equals(GetChannelId(diagnostic.Source), channelId, StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(diagnostic);
            }
        }

        return filtered;
    }

    private void SyncDocumentSubscriptions()
    {
        HashSet<IEditorDocumentViewModel> active = new(GetDocuments());
        foreach (IEditorDocumentViewModel existing in _documentDiagnosticsSubscriptions.Keys.ToList())
        {
            if (!active.Contains(existing))
            {
                _documentDiagnosticsSubscriptions[existing].Dispose();
                _documentDiagnosticsSubscriptions.Remove(existing);
            }
        }

        foreach (IEditorDocumentViewModel doc in active)
        {
            if (_documentDiagnosticsSubscriptions.ContainsKey(doc))
            {
                continue;
            }

            IDisposable subscription = ObserveDocumentDiagnostics(doc)
                .Subscribe(_ =>
                {
                    PublishSnapshotDiagnostics();
                    PublishChannelDiagnostics(doc.FilePath, GetDocumentDiagnostics(doc));
                });

            _documentDiagnosticsSubscriptions[doc] = subscription;
        }
    }

    private IReadOnlyList<IEditorDocumentViewModel> GetDocuments()
    {
        List<IEditorDocumentViewModel> documents = new();
        foreach (IEditorDocumentViewModel doc in _mainViewModel.Documents)
        {
            if (doc is TextDocumentViewModel or DesignerDocumentViewModel)
            {
                documents.Add(doc);
            }
        }

        return documents;
    }

    private IEditorDocumentViewModel? FindDocument(string filePath)
    {
        foreach (IEditorDocumentViewModel doc in GetDocuments())
        {
            if (string.Equals(doc.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return doc;
            }
        }

        return null;
    }

    private static IReadOnlyList<LanguageDiagnostic> GetDocumentDiagnostics(IEditorDocumentViewModel document) => document switch
    {
        TextDocumentViewModel text => text.Diagnostics,
        DesignerDocumentViewModel designer => designer.Diagnostics,
        _ => Array.Empty<LanguageDiagnostic>()
    };

    private static IObservable<Unit> ObserveDocumentDiagnostics(IEditorDocumentViewModel document) => document switch
    {
        TextDocumentViewModel text => Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
            handler => text.Diagnostics.CollectionChanged += handler,
            handler => text.Diagnostics.CollectionChanged -= handler).Select(_ => Unit.Default),
        DesignerDocumentViewModel designer => designer.WhenAnyValue(value => value.Diagnostics).Skip(1).Select(_ => Unit.Default),
        _ => Observable.Empty<Unit>()
    };

    private static IReadOnlyList<DiagnosticsChannelInfo> BuildChannels(IReadOnlyList<LanguageDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return EmptyChannels;
        }

        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        List<DiagnosticsChannelInfo> channels = new();
        foreach (LanguageDiagnostic diagnostic in diagnostics)
        {
            string channelId = GetChannelId(diagnostic.Source);
            if (ids.Add(channelId))
            {
                channels.Add(new DiagnosticsChannelInfo(channelId, channelId));
            }
        }

        return channels;
    }

    private static string GetChannelId(string? source)
    {
        return string.IsNullOrWhiteSpace(source) ? "default" : source;
    }

    private static bool ChannelsEqual(IReadOnlyList<DiagnosticsChannelInfo> left, IReadOnlyList<DiagnosticsChannelInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Id, right[i].Id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
