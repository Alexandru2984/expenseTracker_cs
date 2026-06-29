namespace ExpenseTracker.Api.Infrastructure;

/// <summary>
/// Resolves the real client IP for rate-limiting / partitioning.
/// The app runs behind Cloudflare → Nginx, so we prefer Cloudflare's
/// <c>CF-Connecting-IP</c> header (set only by the Cloudflare edge), then fall
/// back to the connection remote IP (already normalized by ForwardedHeaders).
///
/// IMPORTANT (infra): the origin must only accept traffic from Cloudflare
/// (firewall / Cloudflare Tunnel), otherwise an attacker hitting the origin
/// directly could spoof <c>CF-Connecting-IP</c>. See deploy.md.
/// </summary>
public static class ClientIp
{
    public static string Partition(HttpContext ctx)
    {
        var cf = ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(cf))
            return cf.Trim();

        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
