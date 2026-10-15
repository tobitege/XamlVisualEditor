using System;
using System.Net;
using System.Threading.Tasks;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Viewport;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Integration;

public sealed class PreviewerProtocolTests
{
    [Fact]
    public async Task PreviewerTcpSession_SendsPendingUpdateAfterConnect()
    {
        using PreviewerTcpSession session = new("/tmp/Test.axaml", log: null);
        string xaml = "<UserControl xmlns=\"https://github.com/avaloniaui\" />";
        await session.SendUpdateXamlAsync(xaml, "/tmp/Test.dll", "/Test.axaml", 640, 480);

        TaskCompletionSource<UpdateXamlMessage> updateTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ClientViewportAllocatedMessage> viewportTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IAvaloniaRemoteTransportConnection connection =
            await new BsonTcpTransport().Connect(IPAddress.Loopback, session.Port);
        connection.OnMessage += (_, message) =>
        {
            if (message is ClientViewportAllocatedMessage viewport &&
                Math.Abs(viewport.Width - 640) < 0.1 &&
                Math.Abs(viewport.Height - 480) < 0.1)
                viewportTcs.TrySetResult(viewport);
            if (message is UpdateXamlMessage update) updateTcs.TrySetResult(update);
        };
        connection.Start();
        await Task.WhenAll(updateTcs.Task, viewportTcs.Task).WaitAsync(TimeSpan.FromSeconds(5));

        UpdateXamlMessage updateMessage = await updateTcs.Task;
        Assert.Equal(xaml, updateMessage.Xaml);
        Assert.Equal("/tmp/Test.dll", updateMessage.AssemblyPath);
        Assert.Equal("/Test.axaml", updateMessage.XamlFileProjectPath);
    }
}
