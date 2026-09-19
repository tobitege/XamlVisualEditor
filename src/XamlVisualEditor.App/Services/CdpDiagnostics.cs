#if DEBUG
using System;
using System.Globalization;
using Avalonia.Diagnostics.Cdp;
using Serilog;

namespace XamlVisualEditor.App.Services;

/// <summary>Opt-in loopback diagnostics without desktop mouse or keyboard automation.</summary>
internal sealed class CdpDiagnostics : IDisposable
{
    private bool _started;

    public void StartFromEnvironment()
    {
        string? setting = Environment.GetEnvironmentVariable("XVE_CDP_PORT");
        if (_started || string.IsNullOrWhiteSpace(setting) || setting == "0") return;
        if (!int.TryParse(setting, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port < 1 || port > 65535)
        {
            Log.Warning("XVE_CDP_PORT must be a TCP port from 1 to 65535; CDP remains disabled");
            return;
        }

        try
        {
            CdpServer.Start(port);
            _started = true;
            Log.Information("CDP diagnostics listening at http://127.0.0.1:{CdpPort}/json", port);
        }
        catch (Exception exception)
        {
            CdpServer.Stop();
            Log.Error(exception, "Could not start CDP diagnostics on port {CdpPort}", port);
        }
    }

    public void Dispose()
    {
        if (!_started) return;
        CdpServer.Stop();
        _started = false;
    }
}
#endif
