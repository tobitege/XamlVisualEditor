using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Primitives.Signals;
using XamlVisualEditor.Core;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using PrimitivesLinq = ReactiveUI.Primitives.LinqExtensions;

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
                // FromTask runs the update on the thread that subscribes, which is the throttle timer's thread;
                // the update reads UI objects, so it is subscribed on the main thread.
                return PrimitivesLinq.SubscribeOn(Signal.FromTask(async _ =>
                    {
                        if (!IsDisposed && scheduledRevision == Volatile.Read(ref revision))
                        {
                            await updatePreviewerAsync(this);
                        }
                        return RxVoid.Default;
                    }, RxSchedulers.MainThreadScheduler), RxSchedulers.MainThreadScheduler)
                    .Catch<RxVoid, Exception>(error => Signal.Start(() =>
                    {
                        if (!IsDisposed) SetPreviewerLaunchError(error.Message);
                    }, RxSchedulers.MainThreadScheduler));
            })
            .Concat().Subscribe());
    }
}
