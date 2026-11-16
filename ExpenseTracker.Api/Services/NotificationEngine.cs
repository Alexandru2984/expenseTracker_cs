using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Models;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace ExpenseTracker.Api.Services;

public sealed class NotificationEngine(AppDbContext db, NotificationContent content, VerificationService verification,
    IReminderEmailSender sender, IConfiguration config, TimeProvider clock, ILogger<NotificationEngine> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private sealed record Claim(Guid Id, Guid UserId, Guid LeaseId);
    private static void Finish(NotificationOutbox row, NotificationState state, string code, DateTime now)
    {
        row.State = state; row.ErrorCode = code; row.CompletedAt = now;
        row.LeaseId = null; row.LeaseExpiresAtUtc = null;
    }

    public async Task<int> ScheduleDue(CancellationToken ct = default)
    {
        if (!content.Available) return 0;
        db.ChangeTracker.Clear();
        var now = Now;
        var users = await db.NotificationPreferences.AsNoTracking().Where(p => p.Enabled && p.NextDigestAtUtc <= now)
            .OrderBy(p => p.NextDigestAtUtc).ThenBy(p => p.UserId).Take(50).Select(p => p.UserId).ToListAsync(ct);
        var queued = 0;
        foreach (var userId in users)
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            var user = await db.Users.FindAsync(new object[] { userId }, ct);
            if (user is null) continue;
            try { await verification.LockAccountAsync(user); }
            catch (DbUpdateConcurrencyException) { continue; }
            var pref = await db.NotificationPreferences.SingleOrDefaultAsync(p => p.UserId == userId, ct);
            if (pref is not { Enabled: true } || pref.NextDigestAtUtc is null || pref.NextDigestAtUtc > now) continue;
            var zone = NotificationSchedule.Zone(pref.TimeZone);
            if (zone is null || !user.EmailVerified || string.IsNullOrWhiteSpace(user.Email))
            {
                await NotificationContent.DisableForEmailChange(db, userId, ct);
                await db.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                continue;
            }
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
            pref.NextDigestAtUtc = NotificationSchedule.Next(now, zone, pref.SendHour);
            // Do not replay missed days, or send before today's chosen hour.
            if (NotificationSchedule.AtLocalHour(today, pref.SendHour, zone) <= now
                && !await db.NotificationOutbox.AnyAsync(q => q.UserId == userId && q.LocalDate == today, ct))
            {
                var preview = await content.Preview(userId, pref, today, ct);
                if (preview.HasItems)
                {
                    db.NotificationOutbox.Add(new NotificationOutbox { UserId = userId, LocalDate = today, TimeZone = pref.TimeZone,
                        RecipientHash = content.RecipientHash(userId, user.Email), PreferenceVersion = pref.Version,
                        CreatedAt = now, NextAttemptAtUtc = now });
                    queued++;
                }
            }
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        return queued;
    }

    private async Task<Claim?> ClaimNext(CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var now = Now;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var rows = db.Database.IsRelational()
            ? await db.NotificationOutbox.FromSqlInterpolated($"""
                SELECT * FROM "NotificationOutbox"
                WHERE "State" = 'Pending' AND "NextAttemptAtUtc" <= {now} AND "Attempts" < 3
                ORDER BY "NextAttemptAtUtc", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct)
            : await db.NotificationOutbox.Where(q => q.State == NotificationState.Pending && q.NextAttemptAtUtc <= now && q.Attempts < 3)
                .OrderBy(q => q.NextAttemptAtUtc).ThenBy(q => q.Id).Take(1).ToListAsync(ct);
        var row = rows.SingleOrDefault();
        if (row is null) return null;
        row.State = NotificationState.Preparing; row.LeaseId = Guid.NewGuid(); row.LeaseExpiresAtUtc = now.AddMinutes(2);
        row.Attempts++; row.CompletedAt = null;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(row.Id, row.UserId, row.LeaseId.Value);
    }

    private async Task<MimeMessage?> Prepare(Claim claim, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var user = await db.Users.FindAsync(new object[] { claim.UserId }, ct);
        if (user is null) return null;
        try { await verification.LockAccountAsync(user); }
        catch (DbUpdateConcurrencyException) { return null; } // Lease recovery handles the abandoned preparation.
        var now = Now;
        var row = await db.NotificationOutbox.SingleOrDefaultAsync(q => q.Id == claim.Id && q.UserId == claim.UserId
            && q.State == NotificationState.Preparing && q.LeaseId == claim.LeaseId && q.LeaseExpiresAtUtc > now, ct);
        if (row is null) return null;
        var pref = await db.NotificationPreferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == user.Id, ct);
        var zone = NotificationSchedule.Zone(row.TimeZone);
        var valid = content.Available && pref is { Enabled: true } && user.EmailVerified && !string.IsNullOrWhiteSpace(user.Email)
            && pref.Version == row.PreferenceVersion && zone is not null
            && row.RecipientHash == content.RecipientHash(user.Id, user.Email)
            && row.LocalDate == DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        MimeMessage? message = null;
        if (valid)
        {
            var preview = await content.Preview(user.Id, pref!, row.LocalDate, ct);
            if (preview.HasItems)
            {
                try { message = ReminderMessage.Build(config, row, user.Email!, preview, pref!.IncludeNames); }
                catch (FormatException) { Finish(row, NotificationState.Failed, "invalid_address", now); }
            }
        }
        if (message is not null)
        {
            row.State = NotificationState.Sending; row.LeaseExpiresAtUtc = now.AddMinutes(2);
        }
        else if (row.State != NotificationState.Failed) Finish(row, NotificationState.Canceled, "no_longer_eligible", now);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return null; }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return message;
    }

    private async Task Complete(Claim claim, ReminderSendResult result, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var now = Now;
        var row = await db.NotificationOutbox.SingleOrDefaultAsync(q => q.Id == claim.Id && q.LeaseId == claim.LeaseId
            && q.State == NotificationState.Sending && q.LeaseExpiresAtUtc > now, ct);
        if (row is null) return; // A stale worker cannot overwrite lease recovery.
        if (result == ReminderSendResult.Accepted) Finish(row, NotificationState.Sent, "", now);
        else if (result == ReminderSendResult.Uncertain) Finish(row, NotificationState.Uncertain, "acceptance_unknown", now);
        else if (result == ReminderSendResult.Retryable && row.Attempts < 3)
        {
            row.State = NotificationState.Pending; row.ErrorCode = "temporary_failure";
            row.NextAttemptAtUtc = now.AddMinutes(row.Attempts == 1 ? 5 : 30);
            row.LeaseId = null; row.LeaseExpiresAtUtc = null;
        }
        else Finish(row, NotificationState.Failed, result == ReminderSendResult.Retryable ? "attempts_exhausted" : "rejected", now);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return; }
        if (result != ReminderSendResult.Accepted)
            logger.LogWarning("Notification {NotificationId} result {Result}; attempt {Attempt}", row.Id, result, row.Attempts);
    }

    public async Task<bool> DispatchOne(CancellationToken ct = default)
    {
        if (!content.Available) return false;
        var claim = await ClaimNext(ct);
        if (claim is null) return false;
        var message = await Prepare(claim, ct);
        if (message is null) return true;
        // No database lock is held during SMTP. Consent changes can cancel queued
        // work; a message already being submitted can still arrive.
        var result = ReminderSendResult.Uncertain;
        try { result = content.Available ? await sender.Send(message, ct) : ReminderSendResult.Retryable; }
        catch { /* A sender that throws may already have submitted DATA. Never blindly retry. */ }
        // Persist even when shutdown canceled SMTP; crash recovery is conservative too.
        using var ack = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Complete(claim, result, ack.Token);
        return true;
    }

    public async Task RecoverAndClean(CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        var now = Now;
        await using (var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null)
        {
            var rows = db.Database.IsRelational()
                ? await db.NotificationOutbox.FromSqlInterpolated($"""
                    SELECT * FROM "NotificationOutbox"
                    WHERE "State" IN ('Preparing', 'Sending') AND "LeaseExpiresAtUtc" <= {now}
                    ORDER BY "LeaseExpiresAtUtc", "Id" LIMIT 20 FOR UPDATE SKIP LOCKED
                    """).ToListAsync(ct)
                : await db.NotificationOutbox.Where(q => (q.State == NotificationState.Preparing || q.State == NotificationState.Sending)
                    && q.LeaseExpiresAtUtc <= now).Take(20).ToListAsync(ct);
            foreach (var row in rows)
            {
                if (row.State == NotificationState.Sending) Finish(row, NotificationState.Uncertain, "worker_lost", now);
                else if (row.Attempts >= 3) Finish(row, NotificationState.Failed, "attempts_exhausted", now);
                else
                {
                    row.State = NotificationState.Pending; row.NextAttemptAtUtc = now.AddMinutes(1);
                    row.LeaseId = null; row.LeaseExpiresAtUtc = null; row.ErrorCode = "preparation_interrupted";
                }
            }
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        var cutoff = now.AddDays(-30);
        var old = await db.NotificationOutbox.Where(q => q.CreatedAt < cutoff
            && q.State != NotificationState.Pending && q.State != NotificationState.Preparing && q.State != NotificationState.Sending)
            .OrderBy(q => q.CreatedAt).Take(200).ToListAsync(ct);
        db.NotificationOutbox.RemoveRange(old);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* Another worker cleaned the same terminal rows. */ }
    }
}

public sealed class NotificationWorker(IServiceScopeFactory scopes, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<NotificationEngine>();
                await engine.RecoverAndClean(stoppingToken);
                var count = await engine.ScheduleDue(stoppingToken);
                if (count > 0) logger.LogInformation("Queued {Count} notification digests", count);
                for (var i = 0; i < 10 && !stoppingToken.IsCancellationRequested; i++)
                    if (!await engine.DispatchOne(stoppingToken)) break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogError("Notification worker failed: {ExceptionType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
