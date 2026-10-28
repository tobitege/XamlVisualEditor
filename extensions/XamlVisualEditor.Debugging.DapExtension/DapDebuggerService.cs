using System;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions.Debugging;

namespace XamlVisualEditor.Debugging.Dap;

public sealed class DapDebuggerService : IDebuggerService
{
    private readonly string _adapterId;
    private readonly string _clientId;

    public DapDebuggerService(string adapterId = "netcoredbg", string clientId = "XamlVisualEditor")
    {
        _adapterId = adapterId;
        _clientId = clientId;
    }

    public async Task<IDebugSession> LaunchAsync(DebugLaunchOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.AdapterPath))
        {
            throw new ArgumentException("Adapter path is required.", nameof(options));
        }

        DapDebugAdapterHost host = await DapDebugAdapterHost.StartAsync(options.AdapterPath, ct).ConfigureAwait(false);
        return await StartSessionAsync(new DapDebugSession(host, _adapterId, _clientId),
            session => session.LaunchAsync(options, ct), ct).ConfigureAwait(false);
    }

    public async Task<IDebugSession> AttachAsync(DebugAttachOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.AdapterPath))
        {
            throw new ArgumentException("Adapter path is required.", nameof(options));
        }

        DapDebugAdapterHost host = await DapDebugAdapterHost.StartAsync(options.AdapterPath, ct).ConfigureAwait(false);
        return await StartSessionAsync(new DapDebugSession(host, _adapterId, _clientId),
            session => session.AttachAsync(options, ct), ct).ConfigureAwait(false);
    }

    // Until it is returned nobody else owns the session, so a start the adapter refuses ends the adapter and the
    // program it may already have launched.
    internal static async Task<IDebugSession> StartSessionAsync(
        DapDebugSession session,
        Func<DapDebugSession, Task> start,
        CancellationToken ct)
    {
        try
        {
            await session.InitializeAsync(ct).ConfigureAwait(false);
            await start(session).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
