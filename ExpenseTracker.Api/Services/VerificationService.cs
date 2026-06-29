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

    public VerificationService(AppDbContext db, EmailService email)
    {
        _db = db;
        _email = email;
    }

    /// <summary>Generates a 6-digit code, stores its hash, invalidates older codes, emails it.</summary>
    public async Task IssueAsync(User user, VerificationPurpose purpose)
    {
        var prior = await _db.VerificationCodes
            .Where(c => c.UserId == user.Id && c.Purpose == purpose && c.ConsumedAt == null)
            .ToListAsync();
        foreach (var p in prior) p.ConsumedAt = DateTime.UtcNow;

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _db.VerificationCodes.Add(new VerificationCode
        {
            UserId = user.Id,
            Purpose = purpose,
            CodeHash = AuthTokenService.Hash(code),
            ExpiresAt = DateTime.UtcNow.AddMinutes(TtlMinutes)
        });
        await _db.SaveChangesAsync();

        if (user.Email is not null)
            await _email.SendCodeAsync(user.Email, code, purpose);
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
            Encoding.UTF8.GetBytes(AuthTokenService.Hash(code)));

        if (ok) entry.ConsumedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return ok;
    }
}
