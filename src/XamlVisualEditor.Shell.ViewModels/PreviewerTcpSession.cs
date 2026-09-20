using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Viewport;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed class PreviewerTcpSession : IDisposable
{
    internal const string DesignTreeMessagePrefix = "XVE-DESIGN-TREE ";
    private Guid? _expectedRenderId;
    private DesignerHostSnapshot? _lastDesignTree;
    private Guid? _expectedThemeRequestId;
    private DesignerThemeCatalog? _lastThemeCatalog;
    private readonly string _xamlFilePath;
    private readonly Action<string, string>? _log;
    private readonly IDisposable _listener;
    private readonly object _gate = new();
    private IAvaloniaRemoteTransportConnection? _connection;
    private string? _pendingXaml;
    private string? _pendingAssemblyPath;
    private string? _pendingProjectPath;
    private double? _pendingViewportWidth;
    private double? _pendingViewportHeight;
    private double _pendingDpiX = 96;
    private double _pendingDpiY = 96;
    private double _viewportWidth = 800;
    private double _viewportHeight = 600;
    private double _viewportDpiX = 96;
    private double _viewportDpiY = 96;
    private FrameMessage? _lastFrame;
    private PreviewerErrorInfo? _lastError;

    public PreviewerTcpSession(string xamlFilePath, Action<string, string>? log)
    {
        _xamlFilePath = xamlFilePath;
        _log = log;
        Port = GetFreeTcpPort();
        SessionId = Guid.NewGuid().ToString();
        _listener = new BsonTcpTransport().Listen(IPAddress.Loopback, Port, OnConnected);
    }

    public int Port { get; }

    public string SessionId { get; }

    public event Action<PreviewerErrorInfo>? ErrorReceived;
    public event Action<PreviewerErrorInfo?>? ErrorChanged;
    public event Action<IAvaloniaRemoteTransportConnection?>? ConnectionChanged;
    public event Action<FrameMessage>? FrameReceived;
    public event Action<RequestViewportResizeMessage>? ViewportResizeRequested;
    public event Action<DesignerHostSnapshot?>? DesignTreeChanged;
    public event Action<DesignerThemeCatalog?>? ThemeCatalogChanged;

    public DesignerThemeCatalog? LastThemeCatalog
    {
        get { lock (_gate) return _lastThemeCatalog; }
    }

    internal bool TryReceiveThemeCatalog(string message)
    {
        if (!message.StartsWith(DesignerThemeProtocol.CatalogPrefix, StringComparison.Ordinal)) return false;
        try
        {
            DesignerThemeCatalog? catalog = JsonSerializer.Deserialize<DesignerThemeCatalog>(message.AsSpan(DesignerThemeProtocol.CatalogPrefix.Length));
            if (catalog?.Providers is null) return true;
            lock (_gate)
            {
                if (catalog.RequestId != _expectedThemeRequestId) return true;
                _lastThemeCatalog = catalog;
            }
            ThemeCatalogChanged?.Invoke(catalog);
        }
        catch (JsonException) { ReportError("The previewer returned invalid theme-catalog data."); }
        return true;
    }

    public DesignerHostSnapshot? LastDesignTree
    {
        get { lock (_gate) return _lastDesignTree; }
    }

    internal bool TryReceiveDesignTree(string message)
    {
        if (!message.StartsWith(DesignTreeMessagePrefix, StringComparison.Ordinal)) return false;
        try
        {
            DesignerHostSnapshot? snapshot = JsonSerializer.Deserialize<DesignerHostSnapshot>(message.AsSpan(DesignTreeMessagePrefix.Length));
            if (snapshot is null || snapshot.Nodes is null || snapshot.Nodes.Count == 0
                || !double.IsFinite(snapshot.Width) || !double.IsFinite(snapshot.Height)
                || snapshot.Width < 0 || snapshot.Height < 0) return true;
            HashSet<int> instances = new();
            foreach (DesignerHostNode node in snapshot.Nodes)
                if (node is null || node.NodeId == Guid.Empty || node.InstanceId <= 0
                    || node.ParentInstanceId is { } parent && !instances.Contains(parent)
                    || !instances.Add(node.InstanceId)
                    || !double.IsFinite(node.X) || !double.IsFinite(node.Y)
                    || !double.IsFinite(node.Width) || !double.IsFinite(node.Height)
                    || node.Width < 0 || node.Height < 0
                    || node.Clip is { } clip && (!double.IsFinite(clip.X) || !double.IsFinite(clip.Y)
                        || !double.IsFinite(clip.Width) || !double.IsFinite(clip.Height)
                        || clip.Width < 0 || clip.Height < 0)) return true;
            lock (_gate)
            {
                if (snapshot.RenderId != _expectedRenderId) return true;
                _lastDesignTree = snapshot;
            }
            DesignTreeChanged?.Invoke(snapshot);
        }
        catch (JsonException)
        {
            ReportError("The previewer returned invalid design-tree data.");
        }
        return true;
    }

    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError?.Message;
            }
        }
    }

    public PreviewerErrorInfo? LastErrorInfo
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    public IAvaloniaRemoteTransportConnection? Connection
    {
        get
        {
            lock (_gate)
            {
                return _connection;
            }
        }
    }

    public Task SendUpdateXamlAsync(
        string xaml,
        string assemblyPath,
        string xamlFileProjectPath,
        double? viewportWidth,
        double? viewportHeight,
        Guid? renderId = null,
        Guid? themeRequestId = null)
    {
        if (viewportWidth is { } width && (!double.IsFinite(width) || width <= 0)) viewportWidth = null;
        if (viewportHeight is { } height && (!double.IsFinite(height) || height <= 0)) viewportHeight = null;
        IAvaloniaRemoteTransportConnection? connection;
        bool invalidateTree;
        bool invalidateThemes;
        lock (_gate)
        {
            invalidateThemes = _expectedThemeRequestId != themeRequestId;
            _expectedThemeRequestId = themeRequestId;
            if (invalidateThemes) _lastThemeCatalog = null;
            invalidateTree = _expectedRenderId != renderId;
            _expectedRenderId = renderId;
            if (invalidateTree) _lastDesignTree = null;
            _pendingViewportWidth = viewportWidth ?? _pendingViewportWidth;
            _pendingViewportHeight = viewportHeight ?? _pendingViewportHeight;
            if (_connection is null)
            {
                _pendingXaml = xaml;
                _pendingAssemblyPath = assemblyPath;
                _pendingProjectPath = xamlFileProjectPath;
            }

            connection = _connection;
        }

        if (invalidateTree) DesignTreeChanged?.Invoke(null);
        if (invalidateThemes) ThemeCatalogChanged?.Invoke(null);
        if (connection is null) return Task.CompletedTask;

        UpdateViewportIfNeeded(connection);
        return connection.Send(new UpdateXamlMessage
        {
            Xaml = xaml,
            AssemblyPath = assemblyPath,
            XamlFileProjectPath = xamlFileProjectPath
        });
    }

    public void Dispose()
    {
        _listener.Dispose();
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
            _expectedRenderId = null;
            _lastDesignTree = null;
            _expectedThemeRequestId = null;
            _lastThemeCatalog = null;
        }
        ConnectionChanged?.Invoke(null);
        DesignTreeChanged?.Invoke(null);
        ThemeCatalogChanged?.Invoke(null);
    }

    public FrameMessage? LastFrame
    {
        get
        {
            lock (_gate)
            {
                return _lastFrame;
            }
        }
    }

    internal void ReportError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        SetError(new PreviewerErrorInfo(message, null, null, _xamlFilePath));
    }

    private void SetError(PreviewerErrorInfo? error, IAvaloniaRemoteTransportConnection? origin = null)
    {
        lock (_gate)
        {
            if (origin is not null && !ReferenceEquals(origin, _connection)) return;
            _lastError = error;
        }

        ErrorChanged?.Invoke(error);
        if (error is not null) ErrorReceived?.Invoke(error);
    }

    public void UpdateViewport(double width, double height, double dpiX, double dpiY)
    {
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0 ||
            !double.IsFinite(dpiX) || dpiX <= 0 || !double.IsFinite(dpiY) || dpiY <= 0)
        {
            return;
        }

        IAvaloniaRemoteTransportConnection? connection;
        lock (_gate)
        {
            _pendingViewportWidth = width;
            _pendingViewportHeight = height;
            _pendingDpiX = dpiX;
            _pendingDpiY = dpiY;
            connection = _connection;
        }

        if (connection is null)
        {
            return;
        }

        UpdateViewportIfNeeded(connection);
    }

    private void OnConnected(IAvaloniaRemoteTransportConnection connection)
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = connection;
        }

        _log?.Invoke("Info", "Previewer transport connected");
        ConnectionChanged?.Invoke(connection);

        connection.OnMessage += OnMessage;
        connection.OnException += (_, ex) =>
        {
            string message = $"Previewer transport error: {ex.Message}";
            _log?.Invoke("Error", message);
            SetError(new PreviewerErrorInfo(message, null, null, _xamlFilePath), connection);
        };
        connection.Start();

        SendPreflightMessages(connection);
        _log?.Invoke("Info", "Previewer preflight sent");
        TrySendPendingUpdate(connection);
    }

    private void OnMessage(IAvaloniaRemoteTransportConnection connection, object message)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(connection, _connection)) return;
        }

        if (message is StartDesignerSessionMessage)
        {
            _log?.Invoke("Info", "Previewer session started");
            return;
        }

        if (message is FrameMessage frame)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(connection, _connection)) return;
                _lastFrame = frame;
            }

            connection.Send(new FrameReceivedMessage
            {
                SequenceId = frame.SequenceId
            });

            FrameReceived?.Invoke(frame);
            return;
        }

        if (message is RequestViewportResizeMessage resize)
        {
            _log?.Invoke("Info", $"Previewer viewport resize requested: {resize.Width}x{resize.Height}");
            ViewportResizeRequested?.Invoke(resize);
            return;
        }

        if (message is UpdateXamlResultMessage updateResult)
        {
            if (!string.IsNullOrWhiteSpace(updateResult.Error))
            {
                _log?.Invoke("Error", updateResult.Error);
                int? line = TryGetPositiveInt(updateResult, "LineNumber", "Line");
                int? column = TryGetPositiveInt(updateResult, "LinePosition", "Position", "Column");
                SetError(new PreviewerErrorInfo(updateResult.Error, line, column, _xamlFilePath), connection);
                return;
            }

            _log?.Invoke("Info", "Previewer XAML applied");
            SetError(null, connection);
            return;
        }

        _log?.Invoke("Info", $"Previewer message: {message.GetType().Name}");
    }

    private void SendPreflightMessages(IAvaloniaRemoteTransportConnection connection)
    {
        connection.Send(new ClientSupportedPixelFormatsMessage
        {
            Formats = new[] { PixelFormat.Bgra8888 }
        });

        lock (_gate)
        {
            _pendingViewportWidth ??= 800;
            _pendingViewportHeight ??= 600;
        }
        UpdateViewportIfNeeded(connection, force: true);
    }

    private void TrySendPendingUpdate(IAvaloniaRemoteTransportConnection connection)
    {
        string? xaml;
        string? assemblyPath;
        string? projectPath;

        lock (_gate)
        {
            xaml = _pendingXaml;
            assemblyPath = _pendingAssemblyPath;
            projectPath = _pendingProjectPath;
            _pendingXaml = null;
            _pendingAssemblyPath = null;
            _pendingProjectPath = null;
        }

        if (string.IsNullOrWhiteSpace(xaml) || string.IsNullOrWhiteSpace(assemblyPath))
        {
            return;
        }

        UpdateViewportIfNeeded(connection);
        _ = connection.Send(new UpdateXamlMessage
        {
            Xaml = xaml,
            AssemblyPath = assemblyPath,
            XamlFileProjectPath = projectPath
        });
    }

    private void UpdateViewportIfNeeded(IAvaloniaRemoteTransportConnection connection, bool force = false)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(connection, _connection) ||
                _pendingViewportWidth is not { } width || _pendingViewportHeight is not { } height) return;

            bool dpiChanged = force || Math.Abs(_viewportDpiX - _pendingDpiX) >= 0.01 ||
                Math.Abs(_viewportDpiY - _pendingDpiY) >= 0.01;
            if (!force && !dpiChanged && Math.Abs(_viewportWidth - width) < 0.01 &&
                Math.Abs(_viewportHeight - height) < 0.01) return;

            _viewportWidth = width;
            _viewportHeight = height;
            _viewportDpiX = _pendingDpiX;
            _viewportDpiY = _pendingDpiY;
            if (dpiChanged)
            {
                connection.Send(new ClientRenderInfoMessage { DpiX = _viewportDpiX, DpiY = _viewportDpiY });
            }
            SendViewportMessage(connection, width, height, _viewportDpiX, _viewportDpiY);
        }
    }

    private static int? TryGetPositiveInt(object source, params string[] propertyNames)
    {
        foreach (string name in propertyNames)
        {
            System.Reflection.PropertyInfo? property = source.GetType().GetProperty(name);
            if (property is null)
            {
                continue;
            }

            object? value = property.GetValue(source);
            if (value is int intValue && intValue > 0)
            {
                return intValue;
            }

        }

        return null;
    }

    private static void SendViewportMessage(
        IAvaloniaRemoteTransportConnection connection,
        double width,
        double height,
        double dpiX,
        double dpiY)
    {
        connection.Send(new ClientViewportAllocatedMessage
        {
            Width = width,
            Height = height,
            DpiX = dpiX,
            DpiY = dpiY
        });
    }

    private static int GetFreeTcpPort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
