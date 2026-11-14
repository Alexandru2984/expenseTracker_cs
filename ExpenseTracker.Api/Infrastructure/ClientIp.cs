namespace ExpenseTracker.Api.Infrastructure;

/// <summary>
/// Resolves the real client IP for rate-limiting / partitioning.
/// Nginx verifies the Cloudflare transport and overwrites forwarded headers.
/// ForwardedHeaders normalizes RemoteIpAddress only for an explicitly trusted
/// proxy. Never trust a raw request header here, including CF-Connecting-IP.
/// </summary>
public static class ClientIp
{
    public static string Partition(HttpContext ctx)
    {
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
