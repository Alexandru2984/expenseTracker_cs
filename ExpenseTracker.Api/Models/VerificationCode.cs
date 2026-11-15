namespace ExpenseTracker.Api.Models;

public enum VerificationPurpose
{
    EmailVerification,
    PasswordReset,
    EmailChange
}

/// <summary>
/// A short-lived, single-use code emailed to the user. Only a server-keyed MAC of
/// the code is stored. Brute force is bounded by a per-code attempt counter, a
/// short expiry, and the strict 'auth' rate limiter on the endpoints.
/// </summary>
public class VerificationCode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public VerificationPurpose Purpose { get; set; }

    /// <summary>Server-keyed MAC, bound to the account, inbox and purpose.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ConsumedAt { get; set; }

    public int Attempts { get; set; }
}
