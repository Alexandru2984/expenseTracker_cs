using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using ExpenseTracker.Api.Services;

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
}

public sealed class CreateFinanceTokenRequest
{
    [Required, StringLength(128, MinimumLength = 1)]
    public string CurrentPassword { get; set; } = "";
    [Range(1, 90)]
    public int ValidDays { get; set; } = 30;
}
