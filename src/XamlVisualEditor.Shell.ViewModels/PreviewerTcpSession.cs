using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Viewport;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed class PreviewerTcpSession : IDisposable
{
    internal const string DesignTreeMessagePrefix = "XVE-DESIGN-TREE ";
    /// <summary>Largest frame width and height accepted from the previewer process.</summary>
    internal const int MaxFrameDimension = 16384;
    private bool _invalidFrameLogged;
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
    private string? _latestXaml;
    private readonly Queue<string> _submittedXaml = new();
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
    private bool _errorFromStandardError;

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

    internal void InvalidateDesignTree()
    {
        lock (_gate)
        {
            if (_expectedRenderId is null && _lastDesignTree is null) return;
            _expectedRenderId = null;
            _lastDesignTree = null;
        }
        DesignTreeChanged?.Invoke(null);
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

    public async Task SendUpdateXamlAsync(
        string xaml,
        string assemblyPath,
        string xamlFileProjectPath,
        double? viewportWidth,
        double? viewportHeight,
        Guid? renderId = null,
        Guid? themeRequestId = null,
        Func<Task<bool>>? applyIncremental = null)
    {
        if (viewportWidth is { } width && (!double.IsFinite(width) || width <= 0)) viewportWidth = null;
        if (viewportHeight is { } height && (!double.IsFinite(height) || height <= 0)) viewportHeight = null;
        IAvaloniaRemoteTransportConnection? connection;
        bool invalidateTree;
        bool invalidateThemes;
        lock (_gate)
        {
            _latestXaml = xaml;
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
        if (connection is null) return;

        UpdateViewportIfNeeded(connection);
        if (applyIncremental is not null && await applyIncremental())
        {
            SetError(null, connection);
            _log?.Invoke("Info", "Previewer controls updated without rebuilding the document");
            return;
        }
        await SendTrackedUpdate(connection, new UpdateXamlMessage
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

    internal void ReportStandardErrorLine(string message)
    {
        string line = message.Trim();
        if (((line.StartsWith("at ", StringComparison.Ordinal) || line.StartsWith("bei ", StringComparison.Ordinal))
                && line.Contains('('))
            || (line.StartsWith("---", StringComparison.Ordinal) && !line.StartsWith("--->", StringComparison.Ordinal)))
            return;
        if ((line.StartsWith("File name:", StringComparison.Ordinal) || line.StartsWith("Dateiname:", StringComparison.Ordinal))
            && LastError is not null)
            return;
        const string unhandled = "Unhandled exception.";
        if (line.StartsWith(unhandled, StringComparison.Ordinal)) line = line[unhandled.Length..].TrimStart();
        if (line.StartsWith("--->", StringComparison.Ordinal)) line = line[4..].TrimStart();
        ReportError(line, fromStandardError: true);
    }

    internal void ReportError(string message) => ReportError(message, fromStandardError: false);

    private void ReportError(string message, bool fromStandardError)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        SetError(new PreviewerErrorInfo(message, null, null, _xamlFilePath), fromStandardError: fromStandardError);
    }

    // A crashing preview process closes the connection after writing its exception to the error output.
    // That output names the cause, so the broken connection is only logged then.
    internal void ReportConnectionLost(Exception exception, IAvaloniaRemoteTransportConnection? connection)
    {
        _log?.Invoke("Error", string.Format(UiStrings.PreviewerTransportError, exception.Message));
        lock (_gate)
        {
            if (_errorFromStandardError) return;
        }

        SetError(new PreviewerErrorInfo(UiStrings.PreviewerConnectionLost, null, null, _xamlFilePath), connection);
    }

    private void SetError(PreviewerErrorInfo? error, IAvaloniaRemoteTransportConnection? origin = null, bool fromStandardError = false)
    {
        lock (_gate)
        {
            if (origin is not null && !ReferenceEquals(origin, _connection)) return;
            _lastError = error;
            _errorFromStandardError = fromStandardError;
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
            _submittedXaml.Clear();
        }

        _log?.Invoke("Info", "Previewer transport connected");
        ConnectionChanged?.Invoke(connection);

        connection.OnMessage += OnMessage;
        connection.OnException += (_, ex) => ReportConnectionLost(ex, connection);
        connection.Start();

        SendPreflightMessages(connection);
        _log?.Invoke("Info", "Previewer preflight sent");
        TrySendPendingUpdate(connection);
    }

    /// <summary>
    /// Checks that a frame from the previewer process has a format the surface can copy (Bgra8888, which the session
    /// requests, or Rgb565), dimensions up to
    /// <see cref="MaxFrameDimension"/> and pixel data for every row its stride describes. A frame without size is
    /// valid and shows no image.
    /// </summary>
    internal static bool IsValidFrame(FrameMessage frame)
    {
        if (frame.Width == 0 && frame.Height == 0) return true;
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Width > MaxFrameDimension || frame.Height > MaxFrameDimension)
            return false;
        int bytesPerPixel = frame.Format switch
        {
            PixelFormat.Rgb565 => 2,
            PixelFormat.Bgra8888 => 4,
            _ => 0
        };
        if (bytesPerPixel == 0 || frame.Data is null || frame.Stride < 0) return false;
        long rowBytes = (long)frame.Width * bytesPerPixel;
        long stride = frame.Stride > 0 ? frame.Stride : rowBytes;
        return stride >= rowBytes && frame.Data.LongLength >= stride * (frame.Height - 1) + rowBytes;
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
            bool valid = IsValidFrame(frame);
            lock (_gate)
            {
                if (!ReferenceEquals(connection, _connection)) return;
                if (valid) _lastFrame = frame;
            }

            // An invalid frame is acknowledged as well, so the previewer goes on sending frames.
            connection.Send(new FrameReceivedMessage
            {
                SequenceId = frame.SequenceId
            });

            if (!valid)
            {
                if (!_invalidFrameLogged)
                {
                    _invalidFrameLogged = true;
                    _log?.Invoke("Warning",
                        $"Previewer frame dropped: {frame.Width}x{frame.Height}, stride {frame.Stride}, format {frame.Format}, {frame.Data?.Length ?? 0} bytes");
                }

                return;
            }

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
            string? xaml;
            lock (_gate)
            {
                if (!ReferenceEquals(connection, _connection)) return;
                xaml = _submittedXaml.Count > 0 ? _submittedXaml.Dequeue() : null;
                if (xaml != _latestXaml) return;
            }
            if (!string.IsNullOrWhiteSpace(updateResult.Error))
            {
                PreviewerErrorInfo error = CreateXamlError(updateResult, xaml, _xamlFilePath);
                // A XAML error with a position reaches the output as a diagnostic at its source position; the stack
                // trace and the position in the preview copy only help with other exceptions.
                if (error.Line is null) _log?.Invoke("Error", updateResult.Error);
                SetError(error, connection);
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
        _ = SendTrackedUpdate(connection, new UpdateXamlMessage
        {
            Xaml = xaml,
            AssemblyPath = assemblyPath,
            XamlFileProjectPath = projectPath
        });
    }

    private Task SendTrackedUpdate(IAvaloniaRemoteTransportConnection connection, UpdateXamlMessage update)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(connection, _connection) || update.Xaml != _latestXaml)
                return Task.CompletedTask;
            _submittedXaml.Enqueue(update.Xaml);
            return connection.Send(update);
        }
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

    internal static PreviewerErrorInfo CreateXamlError(UpdateXamlResultMessage result, string? xaml, string filePath)
    {
        int? line = result.Exception?.LineNumber is > 0 ? result.Exception.LineNumber : null;
        int? column = result.Exception?.LinePosition is > 0 ? result.Exception.LinePosition : null;
        PreviewerElementPathSegment[]? path = null;
        XElement? element = null;
        if (line is not null && xaml is not null)
        {
            try
            {
                XDocument document = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
                element = document.Descendants().LastOrDefault(candidate =>
                {
                    IXmlLineInfo location = candidate;
                    return location.LineNumber < line || location.LineNumber == line
                        && (column is null || location.LinePosition <= (long)column + 1);
                });
                path = element is null ? null : CreateElementPath(element);
            }
            catch (XmlException) { }
        }
        // The runtime loader reports text that it tried to add as a control only as a cast without a position.
        if (line is null && xaml is not null && IsTextCastToControl(result.Exception) && FindStrayText(xaml) is { } stray)
        {
            string text = stray.Value.Trim();
            return new PreviewerErrorInfo(string.Format(UiStrings.PreviewerTextNotAllowed, Shorten(text), stray.Parent!.Name.LocalName),
                null, null, filePath, CreateElementPath(stray.Parent), text);
        }
        string message = result.Exception?.Message ?? result.Error;
        string positionSuffix = $" Line {line}, position {column}.";
        if (line is not null && column is not null && message.EndsWith(positionSuffix, StringComparison.Ordinal))
            message = message[..^positionSuffix.Length];
        if (element is not null && DescribeValueError(element, message) is { } described)
            return new PreviewerErrorInfo(described.Message, line, column, filePath, path, described.TextContent, described.AttributeName);
        return new PreviewerErrorInfo(message, line, column, filePath, path);
    }

    private static readonly Regex InvalidNumberMessage = new(@"^The input string '(?<value>.*)' was not in a correct format\.?$");
    private static readonly Regex NoSetterMessage = new(
        @"^Unable to find suitable setter or adder for property (?<property>[\w.]+) of type (?<owner>[\w.]+):\S+ for argument \S*System\.String, available setter parameter lists are:\s*(?<types>.*)$",
        RegexOptions.Singleline);
    private static readonly Regex SingleChildMessage = new(@"^Unable to find a setter that allows multiple assignments to the property (?<property>[\w.]+) of type ");
    private static readonly Regex UnknownPropertyMessage = new(@"^Unable to resolve suitable regular or attached property (?<property>[\w.]+) on type ");
    private static readonly Regex UnknownTypeMessage = new(@"^Unable to resolve type (?<type>[\w.]+) from namespace ");

    // The XAML loader describes value and structure errors in terms of setters and types, and a failed number
    // conversion names only the value. These messages are restated for the element at the error position.
    private static (string Message, string? TextContent, string? AttributeName)? DescribeValueError(XElement element, string message)
    {
        if (InvalidNumberMessage.Match(message) is { Success: true } number)
        {
            XAttribute? numberAttribute = element.Attributes()
                .FirstOrDefault(attribute => !attribute.IsNamespaceDeclaration && attribute.Value == number.Groups["value"].Value);
            return numberAttribute is null ? null
                : (string.Format(UiStrings.PreviewerInvalidNumber, numberAttribute.Value, numberAttribute.Name.LocalName),
                    null, numberAttribute.Name.LocalName);
        }

        if (NoSetterMessage.Match(message) is { Success: true } noSetter)
        {
            if (FindAttribute(element, noSetter.Groups["property"].Value) is { } attribute)
            {
                string[] types = noSetter.Groups["types"].Value
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                string owner = noSetter.Groups["owner"].Value;
                string allowed = types.Select(type => GetEnumNames(type, owner)).FirstOrDefault(names => names.Length > 0) ?? string.Empty;
                string valueType = types.Length == 0 ? noSetter.Groups["property"].Value : types[^1][(types[^1].LastIndexOf('.') + 1)..];
                return (allowed.Length == 0
                        ? string.Format(UiStrings.PreviewerInvalidValue, attribute.Value, attribute.Name.LocalName, valueType)
                        : string.Format(UiStrings.PreviewerInvalidEnumValue, attribute.Value, attribute.Name.LocalName, allowed),
                    null, attribute.Name.LocalName);
            }

            if (element.Nodes().OfType<XText>().FirstOrDefault(node => !string.IsNullOrWhiteSpace(node.Value)) is { } text)
            {
                string content = text.Value.Trim();
                return (string.Format(UiStrings.PreviewerTextNotAllowed, Shorten(content), element.Name.LocalName), content, null);
            }

            return null;
        }

        if (SingleChildMessage.Match(message) is { Success: true } singleChild)
            return (string.Format(UiStrings.PreviewerSingleChild, element.Name.LocalName, singleChild.Groups["property"].Value), null, null);

        if (UnknownPropertyMessage.Match(message) is { Success: true } unknownProperty)
        {
            string property = unknownProperty.Groups["property"].Value;
            return (string.Format(UiStrings.PreviewerUnknownProperty, element.Name.LocalName, property), null,
                FindAttribute(element, property)?.Name.LocalName);
        }

        if (UnknownTypeMessage.Match(message) is { Success: true } unknownType)
            return (string.Format(UiStrings.PreviewerUnknownElement, unknownType.Groups["type"].Value), null, null);

        return null;
    }

    private static XAttribute? FindAttribute(XElement element, string property) =>
        element.Attributes().FirstOrDefault(attribute => !attribute.IsNamespaceDeclaration
            && (attribute.Name.LocalName == property || attribute.Name.LocalName.EndsWith("." + property, StringComparison.Ordinal)));

    // The listed types carry no assembly name; the property owner's assembly is the likeliest home of the enum
    // when no loaded assembly defines it.
    private static string GetEnumNames(string fullTypeName, string ownerAssembly)
    {
        Type? enumType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullTypeName, throwOnError: false))
            .FirstOrDefault(candidate => candidate is { IsEnum: true })
            ?? Type.GetType(fullTypeName + ", " + ownerAssembly, throwOnError: false);
        return enumType is { IsEnum: true } ? string.Join(", ", Enum.GetNames(enumType)) : string.Empty;
    }

    private static string Shorten(string text) => text.Length > 40 ? text[..40] + "…" : text;

    private static readonly HashSet<string> TextElementNames = new(StringComparer.Ordinal)
    {
        "TextBlock", "SelectableTextBlock", "Run", "Span", "Bold", "Italic", "Underline", "Hyperlink", "InlineUIContainer"
    };

    private static PreviewerElementPathSegment[] CreateElementPath(XElement element) =>
        element.AncestorsAndSelf().Reverse().Select(candidate =>
            new PreviewerElementPathSegment(candidate.Name.NamespaceName, candidate.Name.LocalName,
                candidate.ElementsBeforeSelf(candidate.Name).Count())).ToArray();

    private static bool IsTextCastToControl(ExceptionDetails? exception) =>
        exception?.ExceptionType?.EndsWith("InvalidCastException", StringComparison.Ordinal) == true
        && exception.Message?.Contains("System.String", StringComparison.Ordinal) == true;

    // Text in an element that is neither a text element nor a property element; text beside child elements comes
    // first because a panel such as a Grid holds its children that way.
    private static XText? FindStrayText(string xaml)
    {
        try
        {
            List<XText> candidates = XDocument.Parse(xaml, LoadOptions.SetLineInfo).DescendantNodes().OfType<XText>()
                .Where(text => !string.IsNullOrWhiteSpace(text.Value) && text.Parent is { } parent
                    && !parent.Name.LocalName.Contains('.') && !TextElementNames.Contains(parent.Name.LocalName))
                .ToList();
            return candidates.FirstOrDefault(text => text.Parent!.Elements().Any()) ?? candidates.FirstOrDefault();
        }
        catch (XmlException)
        {
            return null;
        }
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
