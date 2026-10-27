using System;
using System.IO;
using System.Threading;
using Porta.Pty;
using XamlVisualEditor.Terminal;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class UnixPtyProcessTests
{
    [Fact]
    public void ResizeAfterDisposeDoesNotReachTheConnection()
    {
        RecordingConnection connection = new();
        UnixPtyProcess process = new(connection, 80, 24);

        process.Dispose();
        process.Resize(100, 40);

        Assert.Equal(0, connection.ResizeCalls);
        Assert.True(SpinWait.SpinUntil(() => connection.Disposed, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void FailedResizeIsRetriedWithTheSameSize()
    {
        RecordingConnection connection = new() { FailNextResize = true };
        UnixPtyProcess process = new(connection, 80, 24);

        process.Resize(100, 40);
        process.Resize(100, 40);
        process.Resize(100, 40);

        Assert.Equal(2, connection.ResizeCalls);
    }

    private sealed class RecordingConnection : IPtyConnection
    {
        public int ResizeCalls { get; private set; }
        public bool FailNextResize { get; set; }
        public volatile bool Disposed;

        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
        public Stream ReaderStream => Stream.Null;
        public Stream WriterStream => Stream.Null;
        public int Pid => 1;
        public int ExitCode => 0;

        public bool WaitForExit(int milliseconds) => true;

        public void Kill()
        {
        }

        public void Resize(int cols, int rows)
        {
            ResizeCalls++;
            if (FailNextResize)
            {
                FailNextResize = false;
                throw new InvalidOperationException("Resize failed.");
            }
        }

        public void Dispose() => Disposed = true;
    }
}
