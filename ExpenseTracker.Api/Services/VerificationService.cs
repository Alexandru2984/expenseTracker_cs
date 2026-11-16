using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Services;

/// <summary>
/// Issues and verifies single-use, hashed email codes. Shared by the auth and
/// account flows. Brute force is bounded by a short expiry + per-code attempt cap.
/// </summary>
public class VerificationService
{
    private const int TtlMinutes = 15;
    private const int MaxAttempts = 5;

    private readonly AppDbContext _db;
    private readonly EmailService _email;
    private readonly AuthTokenService _tokens;

    public VerificationService(AppDbContext db, EmailService email, AuthTokenService tokens)
    {
        _db = db;
        _email = email;
        _tokens = tokens;
    }

    // Save with the caller's credential update to invalidate codes for old credentials.
    public async Task InvalidateAllAsync(Guid userId)
    {
        var codes = await _db.VerificationCodes
            .Where(c => c.UserId == userId && c.ConsumedAt == null).ToListAsync();
        foreach (var entry in codes) entry.ConsumedAt = DateTime.UtcNow;
        var user = await _db.Users.FindAsync(userId);
        if (user is not null) { user.PendingEmail = null; MfaService.ClearSetup(user); }
    }

    public const int EmailCooldownSeconds = 60;

    public static int RetryAfter(User user, DateTime now) => user.VerificationEmailSentAt is { } sent
        ? Math.Max(0, (int)Math.Ceiling((sent.AddSeconds(EmailCooldownSeconds) - now).TotalSeconds)) : 0;

    // All credential/session mutations take the user lock before token/code
    // locks. This avoids deadlocks and closes stale-password session races.
    public async Task LockAccountAsync(User user)
    {
        if (!_db.Database.IsRelational()) return;
        if (_db.Database.CurrentTransaction is null) throw new InvalidOperationException("Account lock requires a transaction.");
        var fresh = (await _db.Users.FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"Id\" = {user.Id} FOR UPDATE")
            .AsNoTracking().ToListAsync()).SingleOrDefault()
            ?? throw new DbUpdateConcurrencyException("Account no longer exists.");
        if (_tokens.CredentialStamp(user) != _tokens.CredentialStamp(fresh))
            throw new DbUpdateConcurrencyException("Credentials changed during the request.");
        _db.Entry(user).CurrentValues.SetValues(fresh);
        _db.Entry(user).OriginalValues.SetValues(fresh);
    }

    /// <summary>Atomically claims a per-account email slot, shared by every purpose and IP.</summary>
    public async Task<VerificationIssueResult> IssueAsync(User user, VerificationPurpose purpose, string? pendingEmail = null)
    {
        // The row lock serializes requests across workers/instances. SMTP is sent
        // after commit, so a slow mail server never holds a database lock open.
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync() : null;
        await LockAccountAsync(user);
        var now = DateTime.UtcNow;
        var retry = RetryAfter(user, now);
        if (retry > 0) return new(false, retry, null);
        var recipient = purpose == VerificationPurpose.EmailChange ? pendingEmail : user.Email;
        if (string.IsNullOrWhiteSpace(recipient)) return new(false, 0, null);
        if (purpose == VerificationPurpose.EmailVerification && user.EmailVerified) return new(false, 0, null);
        if (purpose == VerificationPurpose.EmailChange) user.PendingEmail = recipient;

        var prior = await _db.VerificationCodes
            .Where(c => c.UserId == user.Id && c.Purpose == purpose && c.ConsumedAt == null).ToListAsync();
        foreach (var entry in prior) entry.ConsumedAt = now;
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var expires = now.AddMinutes(TtlMinutes);
        _db.VerificationCodes.Add(new VerificationCode
        {
            UserId = user.Id, Purpose = purpose, CodeHash = _tokens.HashVerificationCode(user, purpose, code),
            ExpiresAt = expires, CreatedAt = now
        });
        user.VerificationEmailSentAt = now;
        await _db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
        await _email.SendCodeAsync(recipient, code, purpose);
        return new(true, EmailCooldownSeconds, expires);
    }

    /// <summary>Validates the most recent unconsumed code; enforces expiry + attempt cap.</summary>
    public async Task<bool> ConsumeAsync(User user, VerificationPurpose purpose, string code)
    {
        var entry = await _db.VerificationCodes
            .Where(c => c.UserId == user.Id && c.Purpose == purpose && c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync();

        if (entry is null || DateTime.UtcNow >= entry.ExpiresAt)
            return false;

        entry.Attempts++;
        if (entry.Attempts > MaxAttempts)
        {
            entry.ConsumedAt = DateTime.UtcNow; // burn after too many tries
            await _db.SaveChangesAsync();
            return false;
        }

        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(entry.CodeHash),
            Encoding.UTF8.GetBytes(_tokens.HashVerificationCode(user, purpose, code)));

        if (ok) entry.ConsumedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync();
            return ok;
        }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }
}

public sealed record VerificationIssueResult(bool Sent, int RetryAfterSeconds, DateTime? ExpiresAt);
