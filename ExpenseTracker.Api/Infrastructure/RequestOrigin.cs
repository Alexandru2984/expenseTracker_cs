namespace ExpenseTracker.Api.Infrastructure;

public static class RequestOrigin
{
    public static bool IsAllowed(HttpRequest request, string[] allowedOrigins)
    {
        var origins = request.Headers.Origin;
        if (origins.Count > 0)
        {
            if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
                || origin.Scheme is not ("http" or "https") || origin.AbsolutePath != "/"
                || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
                return false;
            var authority = origin.GetLeftPart(UriPartial.Authority);
            return string.Equals(authority, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
                || allowedOrigins.Contains(authority, StringComparer.OrdinalIgnoreCase);
        }
        // Non-browser clients have no Origin / Fetch Metadata. A browser that
        // identifies a sibling or cross-site origin must supply an allowed Origin.
        var site = request.Headers["Sec-Fetch-Site"];
        return site.Count == 0 || (site.Count == 1 && site[0] is "same-origin" or "none");
    }
}
