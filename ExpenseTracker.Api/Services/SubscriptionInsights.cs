using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Services;

public sealed record CurrencyAmount(string Currency, decimal Amount);
public sealed record CategoryAmount(string Category, string Currency, decimal MonthlyTotal);
public sealed record PlannedPayment(Guid Id, string Name, decimal Cost, string Currency, DateOnly Date, string Category);
public sealed record ForecastMonth(string Month, int PaymentCount, IReadOnlyList<CurrencyAmount> ByCurrency);
public sealed record SavingsOpportunity(Guid Id, string Name, string Currency, decimal MonthlyTotal);
public sealed record TrialReminder(Guid Id, string Name, DateOnly EndsOn, int DaysRemaining, decimal Cost, string Currency, BillingPeriod BillingPeriod);
public sealed record SubscriptionInsightsResult(DateOnly AsOfDate, int ActiveCount, int InactiveCount,
    int OverdueCount, IReadOnlyList<PlannedPayment> Overdue, IReadOnlyList<PlannedPayment> Upcoming,
    IReadOnlyList<CategoryAmount> ByCategory, IReadOnlyList<ForecastMonth> Forecast,
    IReadOnlyList<CurrencyAmount> PausedMonthly, IReadOnlyList<SavingsOpportunity> SavingsOpportunities,
    int TrialCount, IReadOnlyList<TrialReminder> Trials);

public static class SubscriptionInsights
{
    public static decimal MonthlyCost(SubscriptionItem item) =>
        item.BillingPeriod == BillingPeriod.Yearly ? item.Cost / 12m : item.Cost;

    // Projections never modify stored due dates or imply that payments occurred.
    // Always offset from the original date, so January 31 -> February 28 -> March 31.
    public static IEnumerable<DateOnly> Occurrences(SubscriptionItem item, DateOnly from, DateOnly until)
    {
        if (!Enum.IsDefined(item.BillingPeriod) || item.NextBillingDate == default || item.NextBillingDate >= until)
            yield break;
        var step = item.BillingPeriod == BillingPeriod.Yearly ? 12 : 1;
        var elapsed = (from.Year - item.NextBillingDate.Year) * 12 + from.Month - item.NextBillingDate.Month;
        var index = Math.Max(0, elapsed / step);
        while (true)
        {
            DateOnly date;
            try { date = item.NextBillingDate.AddMonths(index * step); }
            catch (ArgumentOutOfRangeException) { yield break; }
            if (date >= until) yield break;
            if (date >= from) yield return date;
            index++;
        }
    }

    public static SubscriptionInsightsResult Build(IReadOnlyList<SubscriptionItem> items, DateOnly today)
    {
        var active = items.Where(s => s.IsActive).ToList();
        var start = new DateOnly(today.Year, today.Month, 1);
        var end = start.AddMonths(6);
        var payments = active.SelectMany(item => Occurrences(item, today, end)
            .Select(date => new PlannedPayment(item.Id, item.Name, item.Cost, item.Currency, date, item.Category))).ToList();
        var overdue = active.Where(s => s.NextBillingDate < today)
            .OrderBy(s => s.NextBillingDate).ThenBy(s => s.Id).ToList();
        var categories = active.GroupBy(s => new { Category = string.IsNullOrWhiteSpace(s.Category) ? "Altele" : s.Category, s.Currency })
            .Select(g => new CategoryAmount(g.Key.Category, g.Key.Currency, Math.Round(g.Sum(MonthlyCost), 2)))
            .OrderBy(g => g.Category).ThenBy(g => g.Currency).ToList();
        var forecast = Enumerable.Range(0, 6).Select(offset =>
        {
            var month = start.AddMonths(offset);
            var next = month.AddMonths(1);
            var due = payments.Where(p => p.Date >= month && p.Date < next).ToList();
            return new ForecastMonth(month.ToString("yyyy-MM"), due.Count, due.GroupBy(p => p.Currency)
                .Select(g => new CurrencyAmount(g.Key, g.Sum(p => p.Cost))).OrderBy(g => g.Currency).ToList());
        }).ToList();
        var paused = items.Where(s => !s.IsActive).GroupBy(s => s.Currency)
            .Select(g => new CurrencyAmount(g.Key, Math.Round(g.Sum(MonthlyCost), 2))).OrderBy(g => g.Currency).ToList();
        var savings = active.GroupBy(s => s.Currency).OrderBy(g => g.Key)
            .SelectMany(g => g.OrderByDescending(MonthlyCost).ThenBy(s => s.Id).Take(3)
                .Select(s => new SavingsOpportunity(s.Id, s.Name, s.Currency, Math.Round(MonthlyCost(s), 2)))).ToList();
        var trials = active.Where(s => s.TrialEndsOn.HasValue)
            .Select(s => new TrialReminder(s.Id, s.Name, s.TrialEndsOn!.Value, s.TrialEndsOn.Value.DayNumber - today.DayNumber, s.Cost, s.Currency, s.BillingPeriod))
            .OrderBy(s => Math.Abs(s.DaysRemaining)).ThenBy(s => s.EndsOn).ThenBy(s => s.Id).ToList();
        return new(today, active.Count, items.Count - active.Count, overdue.Count,
            overdue.Take(12).Select(s => new PlannedPayment(s.Id, s.Name, s.Cost, s.Currency, s.NextBillingDate, s.Category)).ToList(),
            payments.Where(p => p.Date <= today.AddDays(30)).OrderBy(p => p.Date).ThenBy(p => p.Id).Take(12).ToList(),
            categories, forecast, paused, savings, trials.Count, trials.Take(12).ToList());
    }
}
