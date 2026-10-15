using System.Security.Cryptography;
using System.Text;

namespace XamlVisualEditor.App.Services;

internal enum CdpRequestKind
{
    Forbidden,
    NotFound,
    Version,
    TargetList,
    BrowserSocket,
    PageSocket,
}

internal readonly record struct CdpRequestDecision(CdpRequestKind Kind, string? TargetId = null);

/// <summary>Parsed request line and headers of an HTTP/1.1 request.</summary>
internal sealed class CdpHttpRequestHead
{
    private readonly Dictionary<string, string> _headers;

    private CdpHttpRequestHead(string method, string target, Dictionary<string, string> headers)
    {
        Method = method;
        Target = target;
        _headers = headers;
    }

    public string Method { get; }

    public string Target { get; }

    public bool IsWebSocketUpgrade =>
        string.Equals(GetHeader("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(GetHeader("Sec-WebSocket-Key"));

    public string? GetHeader(string name) => _headers.TryGetValue(name, out string? value) ? value : null;

    public static bool TryParse(string text, out CdpHttpRequestHead? head)
    {
        head = null;
        string[] lines = text.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal)
            || !requestLine[1].StartsWith('/'))
        {
            return false;
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) break;
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) return false;
            string name = lines[i][..colon].Trim();
            // A repeated header is ambiguous for the access check, so the request is refused.
            if (!headers.TryAdd(name, lines[i][(colon + 1)..].Trim())) return false;
        }

        head = new CdpHttpRequestHead(requestLine[0], requestLine[1], headers);
        return true;
    }
}

/// <summary>
/// Access rules of the CDP diagnostics endpoint: every path starts with a random per-process token,
/// and requests from web pages (an Origin header outside the allow list) are refused.
/// </summary>
internal sealed class CdpAccessPolicy
{
    private const string WebSocketAcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly string[] AllowedOrigins = ["devtools://devtools"];
    private readonly byte[] _tokenBytes;

    public CdpAccessPolicy(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Token = token;
        _tokenBytes = Encoding.ASCII.GetBytes(token);
    }

    public string Token { get; }

    public static string CreateToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string ComputeWebSocketAccept(string webSocketKey) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(webSocketKey.Trim() + WebSocketAcceptGuid)));

    public static bool IsOriginAllowed(string? origin) =>
        origin is null || AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

    public string BuildPath(string relativePath) => "/" + Token + relativePath;

    public CdpRequestDecision Evaluate(CdpHttpRequestHead head)
    {
        if (!IsOriginAllowed(head.GetHeader("Origin"))) return new(CdpRequestKind.Forbidden);

        string path = head.Target;
        int queryStart = path.IndexOfAny(['?', '#']);
        if (queryStart >= 0) path = path[..queryStart];

        int tokenEnd = path.IndexOf('/', 1);
        string tokenSegment = tokenEnd < 0 ? path[1..] : path[1..tokenEnd];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(tokenSegment), _tokenBytes))
        {
            return new(CdpRequestKind.Forbidden);
        }

        string relativePath = tokenEnd < 0 ? "/" : path[tokenEnd..];
        if (head.IsWebSocketUpgrade)
        {
            if (!string.Equals(head.Method, "GET", StringComparison.Ordinal)) return new(CdpRequestKind.NotFound);
            if (relativePath == "/devtools/browser") return new(CdpRequestKind.BrowserSocket);
            const string pagePrefix = "/devtools/page/";
            if (relativePath.StartsWith(pagePrefix, StringComparison.Ordinal) && relativePath.Length > pagePrefix.Length)
            {
                return new(CdpRequestKind.PageSocket, Uri.UnescapeDataString(relativePath[pagePrefix.Length..]));
            }

            return new(CdpRequestKind.NotFound);
        }

        if (!string.Equals(head.Method, "GET", StringComparison.Ordinal)) return new(CdpRequestKind.NotFound);
        return relativePath.TrimEnd('/') switch
        {
            "/json/version" => new(CdpRequestKind.Version),
            "/json" or "/json/list" => new(CdpRequestKind.TargetList),
            _ => new(CdpRequestKind.NotFound),
        };
    }
}
