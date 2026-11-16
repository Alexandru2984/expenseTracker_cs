namespace ExpenseTracker.Api.Models;

// No FK: this minimal, pseudonymous receipt survives deletion for restore checks.
public sealed class AccountDeletionRecord
{
    public Guid UserId { get; set; }
    public DateTime DeletedAt { get; set; }
}
