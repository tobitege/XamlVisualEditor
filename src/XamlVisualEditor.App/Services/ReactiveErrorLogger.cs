using System;
using Serilog;

namespace XamlVisualEditor.App.Services;

/// <summary>
/// Receives the errors of reactive pipelines and commands that have no own error handler. ReactiveUI's
/// default handler ends the application; this one writes the error to the log and the Output panel.
/// </summary>
internal sealed class ReactiveErrorLogger : IObserver<Exception>
{
    public void OnNext(Exception value) => Log.Error(value, "Unhandled error in a reactive command or pipeline");

    public void OnError(Exception error) => Log.Error(error, "Unhandled error in a reactive command or pipeline");

    public void OnCompleted()
    {
    }
}
