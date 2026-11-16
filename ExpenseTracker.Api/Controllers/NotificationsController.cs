using System.Security.Claims;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Controllers;

[ApiController, Authorize, Route("api/account/notifications")]
public sealed class NotificationsController(AppDbContext db, VerificationService verification,
    NotificationContent content, TimeProvider clock) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    private ConflictObjectResult Changed() => Conflict(new ProblemDetails { Title =
        "Preferințele au fost modificate între timp. Reîncarcă-le înainte de a salva." });
    private object ResponseFor(NotificationPreference pref, User user) => new
    {
        pref.Enabled, pref.TimeZone, pref.SendHour, pref.LeadDays, pref.Renewals, pref.Trials,
        pref.GoalDeadlines, pref.IncludeNames, pref.Version, pref.NextDigestAtUtc, pref.ConsentRecordedAt,
        available = content.Available, canEnable = user.EmailVerified && !string.IsNullOrWhiteSpace(user.Email),
        recipientContext = content.RecipientHash(user.Id, user.Email ?? ""),
        recipient = user.EmailVerified ? user.Email : null
    };
    private Task<NotificationPreference?> Preference(CancellationToken ct) => db.NotificationPreferences
        .AsNoTracking().SingleOrDefaultAsync(p => p.UserId == UserId, ct);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var user = await db.Users.FindAsync(new object[] { UserId }, ct);
        return user is null ? Unauthorized() : Ok(ResponseFor(await Preference(ct)
            ?? new NotificationPreference { UserId = UserId, Version = Guid.Empty }, user));
    }
    [HttpGet("time-zones")]
    public IActionResult Zones() => Ok(NotificationSchedule.Zones);

    [HttpPut, EnableRateLimiting("writes")]
    public async Task<IActionResult> Save(SaveNotificationPreferenceDto request, CancellationToken ct)
    {
        var user = await db.Users.FindAsync(new object[] { UserId }, ct);
        if (user is null) return Unauthorized();
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        try { await verification.LockAccountAsync(user); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        var preference = await db.NotificationPreferences.SingleOrDefaultAsync(p => p.UserId == UserId, ct);
        if (request.Version != (preference?.Version ?? Guid.Empty)) return Changed();
        if (request.Enabled && (Request.Headers["X-Notification-Recipient"].Count != 1
            || Request.Headers["X-Notification-Recipient"][0] != content.RecipientHash(user.Id, user.Email ?? ""))) return Changed();
        if (request.Enabled && (!user.EmailVerified || string.IsNullOrWhiteSpace(user.Email)))
            return BadRequest(new ProblemDetails { Title = "Verifică adresa curentă de email înainte de a activa notificările." });
        if (request.Enabled && !content.Available)
            return StatusCode(503, new ProblemDetails { Title = "Notificările email sunt indisponibile momentan." });
        var now = clock.GetUtcNow().UtcDateTime;
        if (preference is null) { preference = new NotificationPreference { UserId = UserId }; db.NotificationPreferences.Add(preference); }
        if (request.Enabled && !preference.Enabled) preference.ConsentRecordedAt = now;
        preference.Enabled = request.Enabled; preference.TimeZone = request.TimeZone;
        preference.SendHour = request.SendHour; preference.LeadDays = request.LeadDays;
        preference.Renewals = request.Renewals; preference.Trials = request.Trials;
        preference.GoalDeadlines = request.GoalDeadlines; preference.IncludeNames = request.IncludeNames;
        preference.Version = Guid.NewGuid(); preference.UpdatedAt = now;
        preference.NextDigestAtUtc = request.Enabled
            ? NotificationSchedule.Next(now, NotificationSchedule.Zone(request.TimeZone)!, request.SendHour) : null;
        await NotificationContent.CancelQueued(db, UserId, "preferences_changed", now, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Ok(ResponseFor(preference, user));
    }

    [HttpGet("preview")]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        var preference = await Preference(ct) ?? new NotificationPreference { UserId = UserId };
        var zone = NotificationSchedule.Zone(preference.TimeZone) ?? TimeZoneInfo.Utc;
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime, zone));
        var preview = await content.Preview(UserId, preference, date, ct);
        return Ok(new { preview.LocalDate, preview.LeadDays, preview.RenewalCount, preview.TrialCount, preview.GoalCount,
            preview.Items, messageText = ReminderMessage.Text(preview, preference.IncludeNames) });
    }
    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        return Ok(await db.NotificationOutbox.AsNoTracking().Where(q => q.UserId == UserId && q.CreatedAt >= cutoff)
            .OrderByDescending(q => q.CreatedAt).ThenBy(q => q.Id).Take(20)
            .Select(q => new { q.LocalDate, q.State, q.Attempts, q.CreatedAt, q.CompletedAt, q.ErrorCode }).ToListAsync(ct));
    }
}
