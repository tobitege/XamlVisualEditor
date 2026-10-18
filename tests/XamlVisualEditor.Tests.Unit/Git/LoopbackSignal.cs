using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

/// <summary>
/// A TCP listener on the loopback interface through which a process started by git, such as a hook or a remote
/// helper, tells the test that it runs, and then waits for the test: a script connects with bash's
/// <c>/dev/tcp/127.0.0.1/&lt;port&gt;</c> or with .NET, writes one line and blocks reading until the test closes the
/// connection. A connection the test sees closing although it did not close it means that the process ended.
/// </summary>
internal sealed class LoopbackSignal : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private TcpClient? _client;

    public LoopbackSignal()
    {
        _listener.Start();
    }

    /// <summary>Gets the port the listener accepts on.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// Waits for the process to connect and returns the line it wrote; fails when <paramref name="run"/> ends first.
    /// </summary>
    public async Task<string> WaitForProcessAsync(Task run)
    {
        Task<TcpClient> accept = _listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
        Task first = await Task.WhenAny(accept, run).WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
        Assert.True(first == accept, "git ended before the process it started connected.");
        _client = await accept;
        return await ReadLineAsync(_client.GetStream());
    }

    /// <summary>
    /// Returns whether the process closed the connection, which happens when it ends; fails when the connection is
    /// still open after the wait limit.
    /// </summary>
    public async Task<bool> WaitForCloseAsync()
    {
        NetworkStream stream = (_client ?? throw new InvalidOperationException("No process connected.")).GetStream();
        byte[] buffer = new byte[64];
        try
        {
            int read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(WaitLimit);
            return read == 0;
        }
        catch (IOException)
        {
            // Windows resets the connection of a killed process.
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Lets the process go on: it reads the end of the connection.</summary>
    public void Release()
    {
        _client?.Dispose();
        _client = null;
    }

    public void Dispose()
    {
        Release();
        _listener.Stop();
        _listener.Dispose();
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        StringBuilder line = new();
        byte[] buffer = new byte[1];
        while (await stream.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(WaitLimit) == 1
               && buffer[0] != (byte)'\n')
        {
            line.Append((char)buffer[0]);
        }

        return line.ToString().TrimEnd('\r');
    }
}
