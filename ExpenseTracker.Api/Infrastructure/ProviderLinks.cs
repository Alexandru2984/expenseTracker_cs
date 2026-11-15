namespace ExpenseTracker.Api.Infrastructure;

public static class ProviderLinks
{
    // A user-initiated external link only. Never resolve or fetch it on the server.
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = "";
        if (input is null) return true;
        if (input.Any(char.IsControl)) return false;
        var value = input.Trim();
        if (value.Length == 0) return true;
        if (value.Any(char.IsWhiteSpace) || !Uri.IsWellFormedUriString(value, UriKind.Absolute)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0) return false;
        normalized = uri.AbsoluteUri;
        return normalized.Length <= 2048;
    }
}
