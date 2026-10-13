using System.Reactive.Subjects;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ReactiveUI.Primitives.Concurrency;
using XamlVisualEditor.Extensions.Debugging;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DebugSettingsQueuedDeliveryTests
{
    private const int WriteLimit = 20;

    [AvaloniaFact]
    public void AdapterPathSettlesWhenTheSourceChangesTwiceBeforeQueuedDeliveriesRun()
    {
        // The application delivers through the UI dispatcher queue; the other unit tests deliver inline.
        ISequencer previousScheduler = ReactiveUI.RxSchedulers.MainThreadScheduler;
        ReactiveUI.RxSchedulers.MainThreadScheduler = AvaloniaScheduler.For(Dispatcher.UIThread);
        try
        {
            string sourcePath = "first";
            int writes = 0;
            using Subject<string> changes = new();
            void SetSourcePath(string path)
            {
                // Bounded so that a write-back loop ends the test instead of hanging it.
                if (path == sourcePath || writes >= WriteLimit) return;
                sourcePath = path;
                writes++;
                changes.OnNext(path);
            }

            DebugSettingsViewModel settings = new(
                new DebuggerServiceRegistry(),
                null,
                () => sourcePath,
                SetSourcePath,
                changes,
                () => false,
                _ => { },
                _ => Task.FromResult(false));

            SetSourcePath("second");
            SetSourcePath("third");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("third", sourcePath);
            Assert.Equal("third", settings.AdapterPath);
            Assert.Equal(2, writes);
        }
        finally
        {
            ReactiveUI.RxSchedulers.MainThreadScheduler = previousScheduler;
        }
    }
}
