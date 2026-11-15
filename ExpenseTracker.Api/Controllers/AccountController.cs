using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;

namespace ExpenseTracker.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuthTokenService _tokens;
    private readonly VerificationService _verification;

    public AccountController(AppDbContext db, AuthTokenService tokens, VerificationService verification)
    {
        _db = db;
        _tokens = tokens;
        _verification = verification;
    }

    private Guid GetUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions(CancellationToken ct)
    {
        var userId = GetUserId();
        Guid.TryParse(User.FindFirstValue("session_id"), out var currentId);
        var now = DateTime.UtcNow;
        var sessions = await _db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now && !t.TokenHash.StartsWith("finance:"))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.CreatedAt, t.ExpiresAt, isCurrent = t.Id == currentId }).Take(100).ToListAsync(ct);
        return Ok(sessions);
    }

    [HttpDelete("sessions/{id:guid}"), EnableRateLimiting("writes")]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken ct)
    {
        var userId = GetUserId();
        var session = await _db.RefreshTokens.SingleOrDefaultAsync(t => t.Id == id && t.UserId == userId
            && !t.TokenHash.StartsWith("finance:"), ct);
        if (session is null) return NotFound();
        session.RevokedAt ??= DateTime.UtcNow;
        session.WasRotated = false;
        await _db.SaveChangesAsync(ct);
        if (User.FindFirstValue("session_id") == id.ToString()) _tokens.ClearAuthCookies(Response, Request.IsHttps);
        return NoContent();
    }

    // GET /api/account
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var user = await _db.Users.FindAsync(GetUserId());
        if (user is null) return Unauthorized();

        var now = DateTime.UtcNow;
        var pendingExpiry = await _db.VerificationCodes.Where(c => c.UserId == user.Id
            && c.Purpose == VerificationPurpose.EmailChange && c.ConsumedAt == null && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt).Select(c => (DateTime?)c.ExpiresAt).FirstOrDefaultAsync();
        return Ok(new AccountDto
        {
            Username = user.Username,
            Email = user.Email,
            EmailVerified = user.EmailVerified,
            PendingEmail = user.PendingEmail,
            PendingEmailExpiresAt = pendingExpiry,
            EmailRetryAfterSeconds = VerificationService.RetryAfter(user, now),
            CreatedAt = user.CreatedAt
        });
    }

    // POST /api/account/change-password
    [HttpPost("change-password")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
    {
        if (Encoding.UTF8.GetByteCount(dto.NewPassword) > 72)
            return BadRequest(new ProblemDetails
            {
                Title = "Parolă invalidă.",
                Detail = "Parola depășește limita de 72 de octeți UTF-8."
            });

        var user = await _db.Users.FindAsync(GetUserId());
        if (user is null) return Unauthorized();

        if (!PasswordRules.Verify(dto.CurrentPassword, user.PasswordHash))
            return BadRequest(new ProblemDetails
            {
                Title = "Parolă greșită.",
                Detail = "Parola curentă este incorectă."
            });

        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
        await _verification.LockAccountAsync(user);
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
        await _verification.InvalidateAllAsync(user.Id);

        // Invalidate every existing session, then re-issue one for this device.
        await RevokeAllSessionsAsync(user.Id);
        await _db.SaveChangesAsync();
        var session = await CreateSessionAsync(user);
        if (transaction is not null) await transaction.CommitAsync();
        WriteSession(session);

        return Ok(new { message = "Parola a fost schimbată. Celelalte sesiuni au fost deconectate." });
    }

    // Request a change without replacing the current verified inbox or sessions.
    [HttpPost("change-email")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailDto dto)
    {
        var user = await _db.Users.FindAsync(GetUserId());
        if (user is null) return Unauthorized();

        var email = dto.Email.Trim().ToLowerInvariant();
        if (!PasswordRules.Verify(dto.CurrentPassword, user.PasswordHash))
            return BadRequest(new ProblemDetails { Title = "Parola curentă este incorectă." });
        if (email == user.Email)
            return BadRequest(new ProblemDetails { Title = "Introdu o adresă de email diferită." });
        if (await _db.Users.AnyAsync(u => u.Email == email && u.Id != user.Id))
            return Conflict(new ProblemDetails
            {
                Title = "Email indisponibil.",
                Detail = "Există deja un cont cu acest email."
            });

        var issued = await _verification.IssueAsync(user, VerificationPurpose.EmailChange, email);
        if (!issued.Sent)
        {
            Response.Headers.RetryAfter = issued.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(429, new ProblemDetails { Status = 429, Title = "Așteaptă înainte de a cere alt cod.",
                Detail = $"Poți cere un nou cod peste {issued.RetryAfterSeconds} secunde." });
        }
        return Ok(new { message = "Cod trimis la noua adresă. Adresa curentă rămâne valabilă până la confirmare.",
            pendingEmail = user.PendingEmail, expiresAt = issued.ExpiresAt, retryAfterSeconds = issued.RetryAfterSeconds });
    }

    [HttpPost("confirm-email-change"), EnableRateLimiting("auth")]
    public async Task<IActionResult> ConfirmEmailChange([FromBody] ConfirmEmailChangeDto dto)
    {
        var user = await _db.Users.FindAsync(GetUserId());
        if (user is null) return Unauthorized();
        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
        await _verification.LockAccountAsync(user);
        if (string.IsNullOrEmpty(user.PendingEmail))
            return BadRequest(new ProblemDetails { Title = "Nu există o schimbare de email în așteptare." });
        if (await _db.Users.AnyAsync(u => u.Email == user.PendingEmail && u.Id != user.Id))
            return Conflict(new ProblemDetails { Title = "Adresa a fost ocupată între timp. Alege alta." });

        if (!await _verification.ConsumeAsync(user, VerificationPurpose.EmailChange, dto.Code))
        {
            // Failed attempts must remain counted even when the code is wrong.
            if (transaction is not null) await transaction.CommitAsync();
            return BadRequest(new ProblemDetails { Title = "Cod greșit sau expirat. Cere un cod nou." });
        }
        user.Email = user.PendingEmail;
        user.EmailVerified = true;
        await _verification.InvalidateAllAsync(user.Id);
        await RevokeAllSessionsAsync(user.Id);
        try
        {
            await _db.SaveChangesAsync();
            var session = await CreateSessionAsync(user);
            if (transaction is not null) await transaction.CommitAsync();
            WriteSession(session);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Users_Email" })
        {
            if (transaction is not null) await transaction.RollbackAsync();
            return Conflict(new ProblemDetails { Title = "Adresa a fost ocupată între timp. Alege alta." });
        }
        return Ok(new { message = "Noua adresă a fost confirmată. Celelalte sesiuni și tokenurile Finance au fost revocate." });
    }

    [HttpDelete("pending-email"), EnableRateLimiting("writes")]
    public async Task<IActionResult> CancelEmailChange()
    {
        var user = await _db.Users.FindAsync(GetUserId());
        if (user is null) return Unauthorized();
        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
        await _verification.LockAccountAsync(user);
        user.PendingEmail = null;
        var codes = await _db.VerificationCodes.Where(c => c.UserId == user.Id
            && c.Purpose == VerificationPurpose.EmailChange && c.ConsumedAt == null).ToListAsync();
        foreach (var code in codes) code.ConsumedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
        return NoContent();
    }

    // POST /api/account/logout-all — revokes every session for the user
    [HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll()
    {
        var user = await _db.Users.FindAsync(GetUserId());
        if (user is null) return Unauthorized();
        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
        await _verification.LockAccountAsync(user);
        await RevokeAllSessionsAsync(user.Id);
        await _db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
        _tokens.ClearAuthCookies(Response, Request.IsHttps);
        return NoContent();
    }

    private async Task RevokeAllSessionsAsync(Guid userId)
    {
        var tokens = await _db.RefreshTokens
            .Where(t => t.UserId == userId && (t.RevokedAt == null || t.WasRotated))
            .ToListAsync();
        foreach (var t in tokens) { t.RevokedAt ??= DateTime.UtcNow; t.WasRotated = false; }
    }

    private async Task<(string access, DateTime accessExpires, string refresh, DateTime refreshExpires)> CreateSessionAsync(User user)
    {
        var (rawRefresh, refreshHash, refreshExpires) = _tokens.CreateRefreshToken();

        var session = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAt = refreshExpires
        };
        _db.RefreshTokens.Add(session);
        await _db.SaveChangesAsync();
        var (accessToken, accessExpires) = _tokens.CreateAccessToken(user, session.Id);

        return (accessToken, accessExpires, rawRefresh, refreshExpires);
    }

    private void WriteSession((string access, DateTime accessExpires, string refresh, DateTime refreshExpires) session) =>
        _tokens.WriteAuthCookies(Response, Request.IsHttps, session.access, session.accessExpires, session.refresh, session.refreshExpires);
}
