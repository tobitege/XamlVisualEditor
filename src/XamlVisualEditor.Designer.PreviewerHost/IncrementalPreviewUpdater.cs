using XamlVisualEditor.Core;

namespace XamlVisualEditor.Designer.PreviewerHost;

/// <summary>Applies compatible property changes to the instances that are already rendered.</summary>
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

        List<(Action Apply, Action Undo)> changes = new();
        try
        {
            foreach (DesignerControlUpdate update in updates)
            {
                IReadOnlyList<object> targets = resolve(update.NodeId);
                if (targets.Count == 0) return new(request.RequestId, false);
                foreach (object target in targets)
                {
                    if (RuntimePropertyReset.TryPrepareLayout(target, update.Xaml, update.ClearedProperties, out Action apply)
                        && RuntimePropertyReset.TryPrepareLayout(target, update.PreviousXaml, update.PreviouslyUnsetProperties, out Action undo))
                        changes.Add((apply, undo));
                    else
                    {
                        Action clear = RuntimePropertyReset.Prepare(target, update.Xaml, update.ClearedProperties);
                        Action undoClear = RuntimePropertyReset.Prepare(target, update.PreviousXaml, update.PreviouslyUnsetProperties);
                        changes.Add((() => { clear(); populate(target, update.Xaml); },
                            () => { undoClear(); populate(target, update.PreviousXaml); }));
                    }
                }
            }
        }
        catch (NotSupportedException error) { return new(request.RequestId, false, error.Message); }
        int applied = 0;
        try
        {
            foreach (var change in changes)
            {
                applied++;
                change.Apply();
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
                    changes[index].Undo();
                }
                catch { Reset(); }
            }
            return new(request.RequestId, false, error.GetBaseException().Message);
        }
    }
}
