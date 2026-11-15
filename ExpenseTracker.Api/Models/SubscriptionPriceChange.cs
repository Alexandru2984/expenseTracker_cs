namespace ExpenseTracker.Api.Models;

public class SubscriptionPriceChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public SubscriptionItem Subscription { get; set; } = null!;
    public DateTime ChangedAt { get; set; }
    public decimal PreviousCost { get; set; }
    public string PreviousCurrency { get; set; } = "";
    public BillingPeriod PreviousBillingPeriod { get; set; }
    public decimal Cost { get; set; }
    public string Currency { get; set; } = "";
    public BillingPeriod BillingPeriod { get; set; }
}
