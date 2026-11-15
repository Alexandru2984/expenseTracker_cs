using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using ExpenseTracker.Api.Services;
using ExpenseTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Integrations;

public static class FinanceTokens
{
    public const string Prefix = "fin_";
    public const string HashNamespace = "finance:";
    public const int Length = 47; // 32 random bytes, base64url encoded, plus prefix
    public static string Create() => Prefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    public static bool IsValidFormat(string? value) =>
        value is { Length: Length } && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value[Prefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static string Hash(string value) => HashNamespace + AuthTokenService.Hash(value);

    public static async Task<FinanceGrant?> AuthenticateAsync(HttpRequest request, AppDbContext db, CancellationToken ct)
    {
        var authorization = request.Headers.Authorization;
        if (authorization.Count != 1) return null;
        var header = authorization[0] ?? "";
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[7..];
        if (!IsValidFormat(token)) return null;
        var hash = Hash(token); var now = DateTime.UtcNow;
        return await db.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now)
            .Where(t => db.Users.Any(u => u.Id == t.UserId))
            .Select(t => new FinanceGrant(t.UserId, t.ExpiresAt)).SingleOrDefaultAsync(ct);
    }
}

public sealed record FinanceGrant(Guid UserId, DateTime ExpiresAt);

public sealed class CreateFinanceTokenRequest
{
    [Required, StringLength(128, MinimumLength = 1)]
    public string CurrentPassword { get; set; } = "";
    [Range(1, 90)]
    public int ValidDays { get; set; } = 30;
}
