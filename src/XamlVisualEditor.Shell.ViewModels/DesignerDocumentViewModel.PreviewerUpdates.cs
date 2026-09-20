using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class DesignerDocumentViewModel
{
    private void InitializePreviewerUpdates(Func<DesignerDocumentViewModel, Task>? updatePreviewerAsync)
    {
        if (updatePreviewerAsync is null) return;
        var updates = SyncEngine.SyncEvents.Select(_ => Unit.Default)
            .Merge(DesignSurface.WhenAnyValue(surface => surface.PreviewThemeVariant).Skip(1).Select(_ => Unit.Default))
            .Merge(this.WhenAnyValue(document => document.PreviewThemeRequest).Skip(1).Select(_ => Unit.Default));
        _disposables.Add(updates.Throttle(TimeSpan.FromMilliseconds(250))
            .Select(_ => Observable.FromAsync(() => IsDisposed ? Task.CompletedTask : updatePreviewerAsync(this))
                .SubscribeOn(RxSchedulers.MainThreadScheduler)
                .Catch<Unit, Exception>(error => Observable.Start(() =>
                {
                    if (!IsDisposed) SetPreviewerLaunchError(error.Message);
                }, RxSchedulers.MainThreadScheduler)))
            .Concat().Subscribe());
    }
}
