using XamlVisualEditor.App.Services;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.App;

public sealed class CdpAccessPolicyTests
{
    private const string Token = "0123456789abcdef";

    [Fact]
    public void Evaluate_WebSocketWithTokenAndNoOrigin_IsAccepted()
    {
        CdpAccessPolicy policy = new(Token);

        CdpRequestDecision browser = policy.Evaluate(WebSocketRequest("/" + Token + "/devtools/browser"));
        CdpRequestDecision page = policy.Evaluate(WebSocketRequest("/" + Token + "/devtools/page/target-1"));

        Assert.Equal(CdpRequestKind.BrowserSocket, browser.Kind);
        Assert.Equal(CdpRequestKind.PageSocket, page.Kind);
        Assert.Equal("target-1", page.TargetId);
    }

    [Theory]
    [InlineData("/devtools/browser")]
    [InlineData("/devtools/page/target-1")]
    [InlineData("/wrong-token/devtools/browser")]
    [InlineData("/0123456789abcdeX/devtools/browser")]
    [InlineData("/0123456789abcdef0/devtools/browser")]
    public void Evaluate_WebSocketWithoutValidToken_IsForbidden(string target)
    {
        CdpAccessPolicy policy = new(Token);

        Assert.Equal(CdpRequestKind.Forbidden, policy.Evaluate(WebSocketRequest(target)).Kind);
    }

    [Theory]
    [InlineData("http://attacker.example")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1:9222")]
    public void Evaluate_WebPageOrigin_IsForbiddenEvenWithToken(string origin)
    {
        CdpAccessPolicy policy = new(Token);

        CdpRequestDecision decision = policy.Evaluate(WebSocketRequest("/" + Token + "/devtools/browser", origin));

        Assert.Equal(CdpRequestKind.Forbidden, decision.Kind);
    }

    [Fact]
    public void Evaluate_DevToolsFrontendOrigin_IsAccepted()
    {
        CdpAccessPolicy policy = new(Token);

        CdpRequestDecision decision = policy.Evaluate(
            WebSocketRequest("/" + Token + "/devtools/page/target-1", "devtools://devtools"));

        Assert.Equal(CdpRequestKind.PageSocket, decision.Kind);
    }

    [Theory]
    [InlineData("/json", nameof(CdpRequestKind.TargetList))]
    [InlineData("/json/", nameof(CdpRequestKind.TargetList))]
    [InlineData("/json/list", nameof(CdpRequestKind.TargetList))]
    [InlineData("/json/version", nameof(CdpRequestKind.Version))]
    [InlineData("/json/version?x=1", nameof(CdpRequestKind.Version))]
    [InlineData("/session", nameof(CdpRequestKind.NotFound))]
    public void Evaluate_HttpEndpointsBehindToken(string relativePath, string expectedKind)
    {
        CdpAccessPolicy policy = new(Token);

        Assert.Equal(expectedKind, policy.Evaluate(HttpRequest("GET", "/" + Token + relativePath)).Kind.ToString());
        Assert.Equal(CdpRequestKind.Forbidden, policy.Evaluate(HttpRequest("GET", relativePath)).Kind);
    }

    [Fact]
    public void Evaluate_PostToSession_IsNotServed()
    {
        CdpAccessPolicy policy = new(Token);

        Assert.Equal(CdpRequestKind.NotFound, policy.Evaluate(HttpRequest("POST", "/" + Token + "/session")).Kind);
    }

    [Fact]
    public void TryParse_RejectsDuplicateOriginHeader()
    {
        string text = "GET /" + Token + "/json HTTP/1.1\r\nHost: 127.0.0.1\r\nOrigin: devtools://devtools\r\n"
                      + "Origin: http://attacker.example";

        Assert.False(CdpHttpRequestHead.TryParse(text, out _));
    }

    [Fact]
    public void ComputeWebSocketAccept_MatchesRfc6455Sample()
    {
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", CdpAccessPolicy.ComputeWebSocketAccept("dGhlIHNhbXBsZSBub25jZQ=="));
    }

    [Fact]
    public void CreateToken_IsRandomAndLong()
    {
        string first = CdpAccessPolicy.CreateToken();
        string second = CdpAccessPolicy.CreateToken();

        Assert.Equal(64, first.Length);
        Assert.NotEqual(first, second);
    }

    private static CdpHttpRequestHead WebSocketRequest(string target, string? origin = null)
    {
        string text = "GET " + target + " HTTP/1.1\r\nHost: 127.0.0.1:9222\r\nUpgrade: websocket\r\n"
                      + "Connection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13";
        if (origin is not null) text += "\r\nOrigin: " + origin;
        return Parse(text);
    }

    private static CdpHttpRequestHead HttpRequest(string method, string target) =>
        Parse(method + " " + target + " HTTP/1.1\r\nHost: 127.0.0.1:9222");

    private static CdpHttpRequestHead Parse(string text)
    {
        Assert.True(CdpHttpRequestHead.TryParse(text, out CdpHttpRequestHead? head));
        return head!;
    }
}
