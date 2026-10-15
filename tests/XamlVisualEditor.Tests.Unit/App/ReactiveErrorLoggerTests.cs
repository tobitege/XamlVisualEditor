using System;
using System.Collections.Generic;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using XamlVisualEditor.App.Services;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.App;

public sealed class ReactiveErrorLoggerTests
{
    [Fact]
    public void UnhandledReactiveErrorIsLoggedInsteadOfThrown()
    {
        CollectingSink sink = new();
        ILogger previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        try
        {
            InvalidOperationException failure = new("command failed");

            new ReactiveErrorLogger().OnNext(failure);

            LogEvent logged = Assert.Single(sink.Events);
            Assert.Equal(LogEventLevel.Error, logged.Level);
            Assert.Same(failure, logged.Exception);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
