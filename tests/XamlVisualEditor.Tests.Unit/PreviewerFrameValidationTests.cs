using System.Net;
using System.Threading.Channels;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Viewport;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PreviewerFrameValidationTests
{
    [Theory]
    [InlineData(4, 2, 16, 32, PixelFormat.Bgra8888, true)]
    [InlineData(4, 2, 0, 32, PixelFormat.Bgra8888, true)]
    [InlineData(4, 2, 20, 36, PixelFormat.Bgra8888, true)]
    [InlineData(4, 2, 8, 16, PixelFormat.Rgb565, true)]
    [InlineData(0, 0, 0, 0, PixelFormat.Bgra8888, true)]
    [InlineData(4, 2, 16, 31, PixelFormat.Bgra8888, false)]
    [InlineData(4, 2, 12, 32, PixelFormat.Bgra8888, false)]
    [InlineData(4, 2, -16, 32, PixelFormat.Bgra8888, false)]
    [InlineData(4, 2, 8, 15, PixelFormat.Rgb565, false)]
    [InlineData(4, 2, 16, 32, PixelFormat.Rgba8888, false)]
    [InlineData(-4, 2, 16, 32, PixelFormat.Bgra8888, false)]
    [InlineData(4, 0, 16, 32, PixelFormat.Bgra8888, false)]
    [InlineData(PreviewerTcpSession.MaxFrameDimension + 1, 1, 0, 0, PixelFormat.Bgra8888, false)]
    [InlineData(1, PreviewerTcpSession.MaxFrameDimension + 1, 0, 0, PixelFormat.Bgra8888, false)]
    public void IsValidFrame_checks_size_stride_and_data_length(
        int width, int height, int stride, int dataLength, PixelFormat format, bool expected)
    {
        FrameMessage frame = new()
        {
            Width = width, Height = height, Stride = stride, Format = format, Data = new byte[dataLength]
        };

        Assert.Equal(expected, PreviewerTcpSession.IsValidFrame(frame));
    }

    [Fact]
    public async Task Session_acknowledges_but_drops_a_truncated_frame()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using PreviewerTcpSession session = new("Frames.axaml", log: null);
        List<long> raised = new();
        TaskCompletionSource<bool> validReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FrameReceived += frame =>
        {
            lock (raised) raised.Add(frame.SequenceId);
            if (frame.SequenceId == 2) validReceived.TrySetResult(true);
        };

        using IAvaloniaRemoteTransportConnection connection = await new BsonTcpTransport().Connect(IPAddress.Loopback, session.Port);
        Channel<long> acknowledged = Channel.CreateUnbounded<long>();
        connection.OnMessage += (_, message) =>
        {
            if (message is FrameReceivedMessage received) acknowledged.Writer.TryWrite(received.SequenceId);
        };
        connection.Start();

        await connection.Send(new FrameMessage
        {
            SequenceId = 1, Width = 100, Height = 100, Stride = 400, Format = PixelFormat.Bgra8888, Data = new byte[400]
        });
        await connection.Send(new FrameMessage
        {
            SequenceId = 2, Width = 2, Height = 2, Stride = 8, Format = PixelFormat.Bgra8888, Data = new byte[16]
        });

        await validReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(1, await acknowledged.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        lock (raised) Assert.Equal(new long[] { 2 }, raised);
        Assert.Equal(2, session.LastFrame?.SequenceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frames_of_a_replaced_window_are_acknowledged_only_when_the_reload_fails(bool reloadFails)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using PreviewerTcpSession session = new("Reload.axaml", log: null);
        using IAvaloniaRemoteTransportConnection connection = await new BsonTcpTransport().Connect(IPAddress.Loopback, session.Port);
        Channel<long> acknowledged = Channel.CreateUnbounded<long>();
        TaskCompletionSource<bool> reloadRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnMessage += (_, message) =>
        {
            if (message is FrameReceivedMessage received) acknowledged.Writer.TryWrite(received.SequenceId);
            if (message is Avalonia.Remote.Protocol.Designer.UpdateXamlMessage) reloadRequested.TrySetResult(true);
        };
        connection.Start();
        Task SendFrame(long sequenceId) => connection.Send(new FrameMessage
        {
            SequenceId = sequenceId, Width = 2, Height = 2, Stride = 8, Format = PixelFormat.Bgra8888, Data = new byte[16]
        });
        Task<long> NextAcknowledgement() =>
            acknowledged.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        await SendFrame(41);
        Assert.Equal(41, await NextAcknowledgement());

        await session.SendUpdateXamlAsync("<UserControl />", "Views.dll", "Views.axaml", 160, 80);
        await reloadRequested.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        // The previous window sends another frame before the host answers the reload. A successful reload replaces
        // that window by one that numbers its frames from 1; a failed one keeps it.
        await SendFrame(42);
        await connection.Send(new Avalonia.Remote.Protocol.Designer.UpdateXamlResultMessage { Error = reloadFails ? "Invalid XAML" : null });
        await SendFrame(reloadFails ? 43 : 1);

        // Messages keep their order, so an acknowledgement of frame 42 would arrive before the next one.
        Assert.Equal(reloadFails ? 42 : 1, await NextAcknowledgement());
        if (reloadFails) Assert.Equal(43, await NextAcknowledgement());
    }

    [Fact]
    public async Task Waiting_for_a_stable_frame_ignores_the_stale_frame_and_frames_without_size()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using PreviewerTcpSession session = new("Stable.axaml", log: null);
        using IAvaloniaRemoteTransportConnection connection = await new BsonTcpTransport().Connect(IPAddress.Loopback, session.Port);
        connection.Start();

        async Task SendAndWaitAsync(long sequenceId, int width, int height)
        {
            await connection.Send(new FrameMessage
            {
                SequenceId = sequenceId, Width = width, Height = height, Stride = width * 4,
                Format = PixelFormat.Bgra8888, Data = new byte[width * height * 4]
            });
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (session.LastFrame?.SequenceId != sequenceId) await Task.Delay(10, timeout.Token);
        }

        await SendAndWaitAsync(1, 2, 2);
        FrameMessage stale = session.LastFrame!;
        Assert.Null(await MainWindowViewModel.WaitForStableFrameAsync(session, 500, stale));

        await SendAndWaitAsync(2, 0, 0);
        Assert.Null(await MainWindowViewModel.WaitForStableFrameAsync(session, 500, stale));

        await SendAndWaitAsync(3, 2, 2);
        FrameMessage? frame = await MainWindowViewModel.WaitForStableFrameAsync(session, 10000, stale);
        Assert.Equal(3, frame?.SequenceId);

        // A frame without size that arrives while waiting passes the same filter.
        Task<FrameMessage?> waiting = MainWindowViewModel.WaitForStableFrameAsync(session, 2500, frame);
        await SendAndWaitAsync(4, 0, 0);
        Assert.Null(await waiting);
    }
}
