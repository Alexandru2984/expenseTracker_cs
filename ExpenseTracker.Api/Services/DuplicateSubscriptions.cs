using System.Text;
using System.Text.RegularExpressions;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Services;

public sealed record DuplicateCandidate(Guid Id, string Name, decimal Cost, string Currency, BillingPeriod BillingPeriod,
    DateOnly NextBillingDate, string Category, bool IsActive);
public sealed record DuplicateGroup(string Name, string Currency, decimal ActiveMonthlyTotal, IReadOnlyList<DuplicateCandidate> Items);
public sealed record DuplicateReport(int CheckedCount, bool IncludeInactive, IReadOnlyList<DuplicateGroup> Groups);

public static class DuplicateSubscriptions
{
    // Advisory matching only: never merge, delete or infer that two plans are identical.
    public static string NameKey(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();

    public static DuplicateReport Build(IReadOnlyList<DuplicateCandidate> items, bool includeInactive)
    {
        var candidates = items.Where(s => includeInactive || s.IsActive).ToList();
        var groups = candidates.Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => new { Name = NameKey(s.Name), Currency = s.Currency.Trim().ToUpperInvariant() })
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(g.First().Name.Trim(), g.Key.Currency,
                decimal.Round(g.Where(s => s.IsActive).Sum(s => s.BillingPeriod == BillingPeriod.Yearly ? s.Cost / 12m : s.Cost), 2),
                g.OrderByDescending(s => s.IsActive).ThenBy(s => s.Id).ToArray()))
            .OrderByDescending(g => g.Items.Count).ThenBy(g => g.Name, StringComparer.Ordinal).ThenBy(g => g.Currency, StringComparer.Ordinal).ToArray();
        return new(candidates.Count, includeInactive, groups);
    }
}
