namespace ExpenseTracker.Api.Models;

/// <summary>
/// A persisted, hashed refresh token. The raw token lives only in the client's
/// httpOnly cookie; we store its SHA-256 hash so a DB leak can't be replayed.
/// Tokens are rotated on every refresh (the old one is revoked).
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    /// <summary>SHA-256 (base64) of the raw refresh token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null && DateTime.UtcNow < ExpiresAt;
}
