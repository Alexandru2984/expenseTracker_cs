using System.Security.Cryptography;
using System.Text;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Services;

public sealed class MfaService(AppDbContext db, MfaSecrets secrets, AuthTokenService tokens,
    TimeProvider clock, ILogger<MfaService> logger)
{
    public DateTime Now => clock.GetUtcNow().UtcDateTime;
    public static void ClearSetup(User user)
    {
        user.MfaSetupId = null; user.MfaPendingSecret = null; user.MfaPendingExpiresAt = null; user.MfaSetupAttempts = 0;
    }
    public static string ChallengeHash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("mfa-login:v1:" + raw)));
    public async Task<object?> Challenge(User user, CancellationToken ct)
    {
        var now = Now;
        if (await db.MfaLoginChallenges.CountAsync(c => c.UserId == user.Id && c.ConsumedAt == null && c.ExpiresAt > now, ct) >= 3)
            return null;
        var raw = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = new MfaLoginChallenge { UserId = user.Id, TokenHash = ChallengeHash(raw),
            CredentialStamp = tokens.CredentialStamp(user), ExpiresAt = now.AddMinutes(5) };
        db.MfaLoginChallenges.Add(challenge); await db.SaveChangesAsync(ct);
        return new { requiresTwoFactor = true, challengeToken = raw, expiresAt = challenge.ExpiresAt };
    }
    // Caller holds the user lock. False is a bad proof; decryption/config errors propagate,
    // never falling back to password-only authentication.
    public async Task<bool> Proof(User user, string code, CancellationToken ct)
    {
        var normalized = MfaSecrets.NormalizeRecovery(code);
        if (normalized.Length == 20 && normalized.All(char.IsAsciiHexDigit))
        {
            var hash = MfaSecrets.RecoveryHash(user.Id, normalized);
            var entry = await db.MfaRecoveryCodes.SingleOrDefaultAsync(c => c.UserId == user.Id
                && c.CodeHash == hash && c.ConsumedAt == null, ct);
            if (entry is null) return false;
            entry.ConsumedAt = Now;
            logger.LogWarning("MFA recovery code used for account {AccountId}", user.Id);
            return true;
        }
        if (user.MfaSecret is null) throw new CryptographicException("MFA secret unavailable");
        var step = Totp.Verify(secrets.Unprotect(user.Id, user.MfaSecret), code.Trim(), clock.GetUtcNow(), user.MfaLastStep);
        if (step is null) return false;
        user.MfaLastStep = step; return true;
    }
    public async Task<string[]> NewRecovery(User user, CancellationToken ct)
    {
        var old = await db.MfaRecoveryCodes.Where(c => c.UserId == user.Id).ToListAsync(ct);
        db.MfaRecoveryCodes.RemoveRange(old);
        var codes = Enumerable.Range(0,10).Select(_ => MfaSecrets.NewRecovery()).ToArray();
        db.MfaRecoveryCodes.AddRange(codes.Select(code => new MfaRecoveryCode { UserId = user.Id, CodeHash = MfaSecrets.RecoveryHash(user.Id,code) }));
        return codes;
    }
    public async Task RevokeBrowserSessions(User user, CancellationToken ct)
    {
        var sessions = await db.RefreshTokens.Where(t => t.UserId == user.Id && !t.TokenHash.StartsWith("finance:")
            && (t.RevokedAt == null || t.WasRotated)).ToListAsync(ct);
        foreach (var session in sessions) { session.RevokedAt = Now; session.WasRotated = false; }
        var challenges = await db.MfaLoginChallenges.Where(c => c.UserId == user.Id && c.ConsumedAt == null).ToListAsync(ct);
        foreach (var challenge in challenges) challenge.ConsumedAt = Now;
    }
    public async Task<Action> NewSession(User user, HttpResponse response, bool secure, CancellationToken ct)
    {
        var (raw, hash, expires) = tokens.CreateRefreshToken();
        var session = new RefreshToken { UserId = user.Id, TokenHash = hash, ExpiresAt = expires };
        db.RefreshTokens.Add(session); await db.SaveChangesAsync(ct);
        var (access, accessExpiry) = tokens.CreateAccessToken(user, session.Id);
        return () => tokens.WriteAuthCookies(response,secure,access,accessExpiry,raw,expires);
    }
}
