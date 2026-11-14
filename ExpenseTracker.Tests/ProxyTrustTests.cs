using System.Net;
using ExpenseTracker.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExpenseTracker.Tests;

public class ProxyTrustTests
{
    private static async Task<DefaultHttpContext> Forward(string remote, string xff, string proto, string? networks = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        ctx.Request.Scheme = "http";
        ctx.Request.Headers["X-Forwarded-For"] = xff;
        ctx.Request.Headers["X-Forwarded-Proto"] = proto;
        ctx.Request.Headers["CF-Connecting-IP"] = "203.0.113.250";
        var options = new ForwardedHeadersOptions();
        ProxyTrust.Configure(options, networks);
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
        await middleware.Invoke(ctx);
        return ctx;
    }

    [Fact]
    public async Task Unknown_private_peer_cannot_spoof_client_or_scheme()
    {
        var ctx = await Forward("172.22.0.2", "198.51.100.1", "https");
        Assert.Equal("172.22.0.2", ClientIp.Partition(ctx));
        Assert.Equal("http", ctx.Request.Scheme);
    }

    [Fact]
    public async Task Explicit_gateway_can_forward_one_client_and_scheme()
    {
        var ctx = await Forward("172.22.0.1", "198.51.100.1", "https", "172.22.0.1/32");
        Assert.Equal("198.51.100.1", ClientIp.Partition(ctx));
        Assert.Equal("https", ctx.Request.Scheme);
    }

    [Fact]
    public async Task Additional_injected_hops_cannot_override_nearest_forwarded_client()
    {
        var ctx = await Forward("127.0.0.1", "203.0.113.1, 198.51.100.1", "http, https");
        Assert.Equal("198.51.100.1", ClientIp.Partition(ctx));
        Assert.Equal("https", ctx.Request.Scheme);
    }

    [Fact]
    public async Task Asymmetric_headers_are_ignored()
    {
        var ctx = await Forward("127.0.0.1", "198.51.100.1, 203.0.113.1", "https");
        Assert.Equal("127.0.0.1", ClientIp.Partition(ctx));
        Assert.Equal("http", ctx.Request.Scheme);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData(",")]
    [InlineData("127.0.0.1/32,")]
    [InlineData("127.0.0.1/33")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    public void Invalid_or_universal_trust_configuration_fails_closed(string value)
    {
        Assert.Throws<InvalidOperationException>(() => ProxyTrust.Configure(new ForwardedHeadersOptions(), value));
    }
}
