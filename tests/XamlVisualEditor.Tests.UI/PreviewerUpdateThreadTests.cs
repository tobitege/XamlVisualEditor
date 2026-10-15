using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class PreviewerUpdateThreadTests
{
    [AvaloniaFact]
    public async Task Typed_xaml_reaches_the_previewer_update_on_the_ui_thread()
    {
        bool typed = false;
        TaskCompletionSource<bool> updateRanOnUiThread = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using DesignerDocumentViewModel document = new("Typed.axaml", updatePreviewerAsync: _ =>
        {
            if (typed) updateRanOnUiThread.TrySetResult(Dispatcher.UIThread.CheckAccess());
            return Task.CompletedTask;
        });
        await document.SyncEngine.LoadAsync("<Canvas xmlns='https://github.com/avaloniaui' />");

        // Typing is throttled, so the update starts from the throttle timer's thread.
        typed = true;
        document.SyncEngine.NotifyTextChanged("<Canvas xmlns='https://github.com/avaloniaui' Width='10' />", SyncSource.CodeEditor);

        Assert.True(await updateRanOnUiThread.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
