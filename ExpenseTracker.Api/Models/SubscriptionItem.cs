namespace ExpenseTracker.Api.Models;

public enum BillingPeriod
{
    Monthly,
    Yearly
}

public class SubscriptionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public decimal Cost { get; set; }

    public string Currency { get; set; } = "RON";

    public BillingPeriod BillingPeriod { get; set; } = BillingPeriod.Monthly;

    public DateOnly NextBillingDate { get; set; }

    public string Category { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public string Notes { get; set; } = string.Empty;
    public string ProviderUrl { get; set; } = string.Empty;
    public DateOnly? TrialEndsOn { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;
}

