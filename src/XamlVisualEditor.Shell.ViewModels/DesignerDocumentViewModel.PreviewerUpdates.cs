using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class DesignerDocumentViewModel
{
    private void InitializePreviewerUpdates(Func<DesignerDocumentViewModel, Task>? updatePreviewerAsync)
    {
        int revision = 0;
        var updates = SyncEngine.SyncEvents.Select(change => change.Source == SyncSource.CodeEditor
                ? TimeSpan.FromMilliseconds(250) : TimeSpan.Zero)
            .Merge(DesignSurface.WhenAnyValue(surface => surface.PreviewThemeVariant).Skip(1).Select(_ => TimeSpan.Zero))
            .Merge(this.WhenAnyValue(document => document.PreviewThemeRequest).Skip(1).Select(_ => TimeSpan.Zero))
            .Do(_ => { Interlocked.Increment(ref revision); PreviewerSession?.InvalidateDesignTree(); });
        if (updatePreviewerAsync is null)
        {
            _disposables.Add(updates.Subscribe());
            return;
        }
        _disposables.Add(updates.Throttle(delay => delay == TimeSpan.Zero
                ? Observable.Return(Unit.Default) : Observable.Timer(delay).Select(_ => Unit.Default))
            .Select(_ =>
            {
                int scheduledRevision = Volatile.Read(ref revision);
                return Observable.FromAsync(() => IsDisposed || scheduledRevision != Volatile.Read(ref revision)
                        ? Task.CompletedTask : updatePreviewerAsync(this))
                    .SubscribeOn(RxSchedulers.MainThreadScheduler)
                    .Catch<Unit, Exception>(error => Observable.Start(() =>
                    {
                        if (!IsDisposed) SetPreviewerLaunchError(error.Message);
                    }, RxSchedulers.MainThreadScheduler));
            })
            .Concat().Subscribe());
    }
}
