using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Services;

// Legacy files without IDs may skip only the same normalized saved content,
// never an unrelated plan that happens to have the same display name.
public sealed record SubscriptionImportKey(string Name, decimal Cost, string Currency, BillingPeriod BillingPeriod,
    DateOnly NextBillingDate, string Category, bool IsActive, string Notes, string ProviderUrl, DateOnly? TrialEndsOn)
{
    public static SubscriptionImportKey From(SubscriptionItem item) => new(item.Name.Trim(), item.Cost, item.Currency.Trim().ToUpperInvariant(),
        item.BillingPeriod, item.NextBillingDate, item.Category.Trim(), item.IsActive,
        item.Notes.Replace("\r\n", "\n").Replace('\r', '\n'), item.ProviderUrl, item.TrialEndsOn);
}
