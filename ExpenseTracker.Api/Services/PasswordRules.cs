using System.Text;

namespace ExpenseTracker.Api.Services;

public static class PasswordRules
{
    public const string MfaPrefix = "$mfa$";
    public static string WithoutMfa(string hash) => hash.StartsWith(MfaPrefix, StringComparison.Ordinal) ? hash[MfaPrefix.Length..] : hash;
    public static string Hash(string password, bool mfaEnabled) =>
        (mfaEnabled ? MfaPrefix : "") + BCrypt.Net.BCrypt.HashPassword(password);
    public static bool Verify(string password, string hash)
    {
        if (Encoding.UTF8.GetByteCount(password) > 72) return false;
        try { return BCrypt.Net.BCrypt.Verify(password, WithoutMfa(hash)); }
        catch { return false; } // Corrupt/unsupported stored hashes fail closed.
    }
}
