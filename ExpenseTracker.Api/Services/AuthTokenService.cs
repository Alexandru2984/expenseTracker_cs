using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Services;

/// <summary>
/// Issues short-lived access tokens (JWT) and long-lived rotating refresh
/// tokens, and manages the httpOnly auth cookies + the readable CSRF cookie.
/// The raw refresh token only ever lives in the client cookie; the DB stores
/// its SHA-256 hash.
/// </summary>
public class AuthTokenService
{
    public const string AccessCookie = "access_token";
    public const string RefreshCookie = "refresh_token";
    public const string CsrfCookie = "csrf_token";
    public const string CsrfHeader = "X-CSRF-Token";
    public const string AccountContextHeader = "X-Account-Context";

    // Refresh cookie is only sent to the auth endpoints that need it.
    public const string RefreshPath = "/api/auth";

    private readonly IConfiguration _config;

    public AuthTokenService(IConfiguration config) => _config = config;

    private int AccessMinutes => _config.GetValue<int?>("Jwt:AccessTokenMinutes") ?? 15;
    public int RefreshDays => _config.GetValue<int?>("Jwt:RefreshTokenDays") ?? 14;

    public (string token, DateTime expiresAt) CreateAccessToken(User user, Guid sessionId)
    {
        var secret = _config["Jwt:Secret"]!;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiresAt = DateTime.UtcNow.AddMinutes(AccessMinutes);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("session_id", sessionId.ToString()),
            new Claim("credential_stamp", CredentialStamp(user))
        };

        var jwt = new JwtSecurityToken(
            issuer: "ExpenseTracker",
            audience: "ExpenseTracker",
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(jwt), expiresAt);
    }

    /// <summary>Returns the raw token (goes in the cookie) and its hash (goes in the DB).</summary>
    public (string raw, string hash, DateTime expiresAt) CreateRefreshToken()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (raw, Hash(raw), DateTime.UtcNow.AddDays(RefreshDays));
    }

    public static string Hash(string raw) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    // An opaque identity binding, not an authentication credential. Stable across
    // refreshes and credential updates, distinct from every other MAC purpose.
    public string AccountContext(Guid userId) => Convert.ToHexString(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(_config["Jwt:Secret"]!), Encoding.UTF8.GetBytes($"browser-account:v1:{userId:N}")));

    public bool MatchesAccountContext(HttpRequest request, Guid userId)
    {
        // Optional for existing API clients; the SPA always binds authenticated requests.
        if (!request.Headers.TryGetValue(AccountContextHeader, out var values)) return true;
        return values.Count == 1 && values[0] is { Length: 64 } context
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context), Encoding.UTF8.GetBytes(AccountContext(userId)));
    }

    // Bind JWTs to current credentials without putting password hashes in tokens.
    public string CredentialStamp(User user) => Convert.ToBase64String(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(_config["Jwt:Secret"]!),
        Encoding.UTF8.GetBytes($"{user.PasswordHash}\n{user.Email}\n{user.EmailVerified}")));

    // Numeric codes have only a million possibilities. A server-keyed MAC keeps
    // a database-only disclosure from enabling an offline dictionary attack.
    public string HashVerificationCode(User user, VerificationPurpose purpose, string code) =>
        "v2:" + Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_config["Jwt:Secret"]!),
            Encoding.UTF8.GetBytes($"verification:{user.Id:D}:{purpose}:{user.Email}:{(purpose == VerificationPurpose.EmailChange ? user.PendingEmail + ":" : "")}{code}")));

    public static string CreateCsrfToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    public void WriteAuthCookies(HttpResponse res, bool secure,
        string accessToken, DateTime accessExpiresAt,
        string refreshToken, DateTime refreshExpiresAt)
    {
        res.Cookies.Append(AccessCookie, accessToken, CookieOpts(secure, "/", accessExpiresAt));
        res.Cookies.Append(RefreshCookie, refreshToken, CookieOpts(secure, RefreshPath, refreshExpiresAt));
    }

    public void ClearAuthCookies(HttpResponse res, bool secure)
    {
        res.Cookies.Delete(AccessCookie, new CookieOptions
        {
            Secure = secure, SameSite = SameSiteMode.Strict, Path = "/"
        });
        res.Cookies.Delete(RefreshCookie, new CookieOptions
        {
            Secure = secure, SameSite = SameSiteMode.Strict, Path = RefreshPath
        });
    }

    private static CookieOptions CookieOpts(bool secure, string path, DateTime expiresAt) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Strict,
        Path = path,
        Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc))
    };
}
