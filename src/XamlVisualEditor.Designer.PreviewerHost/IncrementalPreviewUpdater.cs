using XamlVisualEditor.Core;

namespace XamlVisualEditor.Designer.PreviewerHost;

/// <summary>Übernimmt kompatible Eigenschaftsänderungen in die bereits gerenderten Instanzen.</summary>
internal sealed class IncrementalPreviewUpdater
{
    private string? _xaml;
    private Guid _renderId;
    private Action<object, string>? _populate;

    public void Reset() { _xaml = null; _populate = null; }

    public void Record(string xaml, Guid renderId, Action<object, string> populate)
    {
        _xaml = xaml;
        _renderId = renderId;
        _populate = populate;
    }

    public DesignerUpdateResult Apply(DesignerUpdateRequest request, Func<Guid, IReadOnlyList<object>> resolve,
        Action<Guid> completed)
    {
        if (_xaml is null || _populate is null || request.BaseRenderId != _renderId
            || !DesignerUpdateProtocol.TryCreateUpdates(_xaml, request.Xaml, out IReadOnlyList<DesignerControlUpdate> updates))
            return new(request.RequestId, false);
        Action<object, string> populate = _populate;

        List<(object Control, DesignerControlUpdate Update, Action Clear, Action UndoClear)> changes = new();
        try
        {
            foreach (DesignerControlUpdate update in updates)
            {
                IReadOnlyList<object> targets = resolve(update.NodeId);
                if (targets.Count == 0) return new(request.RequestId, false);
                foreach (object target in targets)
                    changes.Add((target, update,
                        RuntimePropertyReset.Prepare(target, update.Xaml, update.ClearedProperties),
                        RuntimePropertyReset.Prepare(target, update.PreviousXaml, update.PreviouslyUnsetProperties)));
            }
        }
        catch (NotSupportedException) { return new(request.RequestId, false); }
        int applied = 0;
        try
        {
            foreach (var change in changes)
            {
                applied++;
                change.Clear();
                populate(change.Control, change.Update.Xaml);
            }
            completed(request.RenderId);
            _xaml = request.Xaml;
            _renderId = request.RenderId;
            return new(request.RequestId, true);
        }
        catch (Exception error)
        {
            for (int index = applied - 1; index >= 0; index--)
            {
                try
                {
                    changes[index].UndoClear();
                    populate(changes[index].Control, changes[index].Update.PreviousXaml);
                }
                catch { Reset(); }
            }
            return new(request.RequestId, false, error.GetBaseException().Message);
        }
    }
}
