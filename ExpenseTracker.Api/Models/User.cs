namespace ExpenseTracker.Api.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Username { get; set; } = string.Empty;

    // Nullable: accounts created before email verification existed have no email.
    // New registrations always set it. See AddEmailVerification migration.
    public string? Email { get; set; }

    public bool EmailVerified { get; set; }

    public string? PendingEmail { get; set; }

    public DateTime? VerificationEmailSentAt { get; set; }

    public int LoginFailureCount { get; set; }
    public DateTime? LastLoginFailureAt { get; set; }
    public DateTime? LoginBlockedUntil { get; set; }

    public string PasswordHash { get; set; } = string.Empty;
    public bool MfaEnabled { get; set; }
    public Guid MfaEpoch { get; set; }
    public string? MfaSecret { get; set; }
    public long? MfaLastStep { get; set; }
    public Guid? MfaSetupId { get; set; }
    public string? MfaPendingSecret { get; set; }
    public DateTime? MfaPendingExpiresAt { get; set; }
    public DateTime? MfaSetupStartedAt { get; set; }
    public int MfaSetupAttempts { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
