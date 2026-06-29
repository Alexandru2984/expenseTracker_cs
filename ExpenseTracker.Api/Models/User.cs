namespace ExpenseTracker.Api.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Username { get; set; } = string.Empty;

    // Nullable: accounts created before email verification existed have no email.
    // New registrations always set it. See AddEmailVerification migration.
    public string? Email { get; set; }

    public bool EmailVerified { get; set; }

    public string PasswordHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
