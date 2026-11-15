using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;
using System.Data;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Services;

public static class PriceHistory
{
    // Ownership is enforced at the query boundary, including callers with Finance grants.
    public static async Task<PriceHistoryDto?> ReadAsync(AppDbContext db, Guid userId, Guid subscriptionId,
        int skip, int take, CancellationToken ct)
    {
        // Keep current price, total and page on one snapshot without locking writers.
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var current = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Id == subscriptionId && s.UserId == userId)
            .Select(s => new PricePointDto(s.Cost, s.Currency, s.BillingPeriod)).SingleOrDefaultAsync(ct);
        if (current is null) return null;
        skip = Math.Clamp(skip, 0, 1_000_000); take = Math.Clamp(take, 1, 100);
        var query = db.SubscriptionPriceChanges.AsNoTracking()
            .Where(c => c.SubscriptionId == subscriptionId && c.Subscription.UserId == userId);
        var total = await query.CountAsync(ct);
        var changes = await query.OrderByDescending(c => c.ChangedAt).ThenByDescending(c => c.Id)
            .Skip(skip).Take(take).ToListAsync(ct);
        var entries = changes.Select(c =>
        {
            var previousMonthly = Monthly(c.PreviousCost, c.PreviousBillingPeriod);
            var currentMonthly = Monthly(c.Cost, c.BillingPeriod);
            var comparable = c.Currency == c.PreviousCurrency;
            return new PriceChangeDto(c.Id, c.ChangedAt,
                new(c.PreviousCost, c.PreviousCurrency, c.PreviousBillingPeriod), new(c.Cost, c.Currency, c.BillingPeriod),
                comparable ? decimal.Round(currentMonthly - previousMonthly, 2) : null,
                comparable && previousMonthly > 0 ? decimal.Round((currentMonthly - previousMonthly) / previousMonthly * 100m, 2) : null);
        }).ToArray();
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(subscriptionId, current, total, skip, take, entries);
    }

    private static decimal Monthly(decimal cost, BillingPeriod period) => period == BillingPeriod.Yearly ? cost / 12m : cost;
}
