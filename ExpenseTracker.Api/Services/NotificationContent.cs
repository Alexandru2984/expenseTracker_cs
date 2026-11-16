using System.Security.Cryptography;
using System.Text;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Services;

public sealed class NotificationContent(AppDbContext db, IConfiguration config, IReminderEmailSender sender)
{
    public bool Available => config.GetValue("Notifications:Enabled", true)
        && sender.IsConfigured;
    public string RecipientHash(Guid id, string email) => Convert.ToHexString(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(config["Jwt:Secret"]!),
        Encoding.UTF8.GetBytes($"notification-recipient:v1:{id:N}:{email.Trim().ToLowerInvariant()}")));

    public async Task<NotificationPreviewDto> Preview(Guid userId, NotificationPreference preference, DateOnly today, CancellationToken ct)
    {
        var end = today.AddDays(preference.LeadDays);
        var renewals = db.Subscriptions.AsNoTracking().Where(s => s.UserId == userId && s.IsActive
            && s.NextBillingDate >= today && s.NextBillingDate <= end);
        var trials = db.Subscriptions.AsNoTracking().Where(s => s.UserId == userId && s.IsActive
            && s.TrialEndsOn >= today && s.TrialEndsOn <= end);
        var goals = db.SavingsGoals.AsNoTracking().Where(g => g.UserId == userId && !g.IsArchived
            && g.TargetDate >= today && g.TargetDate <= end
            && (g.Contributions.Sum(c => (decimal?)c.Amount) ?? 0) < g.TargetAmount);
        var renewalCount = preference.Renewals ? await renewals.CountAsync(ct) : 0;
        var trialCount = preference.Trials ? await trials.CountAsync(ct) : 0;
        var goalCount = preference.GoalDeadlines ? await goals.CountAsync(ct) : 0;
        var items = new List<NotificationItemDto>();
        if (preference.Renewals) items.AddRange(await renewals.OrderBy(s => s.NextBillingDate).ThenBy(s => s.Id).Take(20)
            .Select(s => new NotificationItemDto("renewal", s.Name, s.NextBillingDate)).ToListAsync(ct));
        if (preference.Trials) items.AddRange(await trials.OrderBy(s => s.TrialEndsOn).ThenBy(s => s.Id).Take(20)
            .Select(s => new NotificationItemDto("trial", s.Name, s.TrialEndsOn!.Value)).ToListAsync(ct));
        if (preference.GoalDeadlines) items.AddRange(await goals.OrderBy(g => g.TargetDate).ThenBy(g => g.Id).Take(20)
            .Select(g => new NotificationItemDto("goal", g.Name, g.TargetDate!.Value)).ToListAsync(ct));
        return new(today, preference.LeadDays, renewalCount, trialCount, goalCount,
            items.OrderBy(i => i.Date).ThenBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.Ordinal).Take(20).ToArray());
    }

    // Caller holds the account lock inside a transaction.
    public static async Task CancelQueued(AppDbContext db, Guid userId, string reason, DateTime now, CancellationToken ct)
    {
        var query = db.NotificationOutbox.Where(q => q.UserId == userId
            && (q.State == NotificationState.Pending || q.State == NotificationState.Preparing));
        if (db.Database.IsRelational())
        {
            // A concurrent claim must not make opt-out fail with a stale row version.
            await query.ExecuteUpdateAsync(set => set.SetProperty(q => q.State, NotificationState.Canceled)
                .SetProperty(q => q.ErrorCode, reason).SetProperty(q => q.CompletedAt, now)
                .SetProperty(q => q.LeaseId, (Guid?)null).SetProperty(q => q.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(q => q.Version, Guid.NewGuid()), ct);
        }
        else foreach (var row in await query.ToListAsync(ct))
        {
            row.State = NotificationState.Canceled; row.ErrorCode = reason; row.CompletedAt = now;
            row.LeaseId = null; row.LeaseExpiresAtUtc = null;
        }
    }

    // Caller holds the account lock inside a transaction.
    public static async Task DisableForEmailChange(AppDbContext db, Guid userId, CancellationToken ct = default)
    {
        var preference = await db.NotificationPreferences.FindAsync(new object[] { userId }, ct);
        if (preference is not null)
        {
            preference.Enabled = false; preference.NextDigestAtUtc = null;
            preference.Version = Guid.NewGuid(); preference.UpdatedAt = DateTime.UtcNow;
        }
        await CancelQueued(db, userId, "email_changed", DateTime.UtcNow, ct);
    }
}
