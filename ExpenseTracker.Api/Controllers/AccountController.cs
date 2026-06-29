using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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

    // GET /api/account
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var user = await _db.Users.FindAsync(GetUserId());
        if (user is null) return Unauthorized();

        return Ok(new AccountDto
        {
            Username = user.Username,
            Email = user.Email,
            EmailVerified = user.EmailVerified,
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
                Detail = "Parola este prea lungă (maxim 72 de caractere)."
            });

        var user = await _db.Users.FindAsync(GetUserId());
        if (user is null) return Unauthorized();

        if (!BCrypt.Net.BCrypt.Verify(dto.CurrentPassword, user.PasswordHash))
            return BadRequest(new ProblemDetails
            {
                Title = "Parolă greșită.",
                Detail = "Parola curentă este incorectă."
            });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);

        // Invalidate every existing session, then re-issue one for this device.
        await RevokeAllSessionsAsync(user.Id);
        await _db.SaveChangesAsync();
        await IssueSessionAsync(user);

        return Ok(new { message = "Parola a fost schimbată. Celelalte sesiuni au fost deconectate." });
    }

    // POST /api/account/change-email — sets a new (unverified) email and emails a code.
    [HttpPost("change-email")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailDto dto)
    {
        var user = await _db.Users.FindAsync(GetUserId());
        if (user is null) return Unauthorized();

        var email = dto.Email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email == email && u.Id != user.Id))
            return Conflict(new ProblemDetails
            {
                Title = "Email indisponibil.",
                Detail = "Există deja un cont cu acest email."
            });

        user.Email = email;
        user.EmailVerified = false;
        await _db.SaveChangesAsync();

        await _verification.IssueAsync(user, VerificationPurpose.EmailVerification);

        return Ok(new { message = "Ți-am trimis un cod de verificare la noua adresă." });
    }

    // POST /api/account/logout-all — revokes every session for the user
    [HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll()
    {
        await RevokeAllSessionsAsync(GetUserId());
        await _db.SaveChangesAsync();
        _tokens.ClearAuthCookies(Response, Request.IsHttps);
        return NoContent();
    }

    private async Task RevokeAllSessionsAsync(Guid userId)
    {
        var tokens = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync();
        foreach (var t in tokens) t.RevokedAt = DateTime.UtcNow;
    }

    private async Task IssueSessionAsync(User user)
    {
        var (accessToken, accessExpires) = _tokens.CreateAccessToken(user);
        var (rawRefresh, refreshHash, refreshExpires) = _tokens.CreateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAt = refreshExpires
        });
        await _db.SaveChangesAsync();

        _tokens.WriteAuthCookies(Response, Request.IsHttps,
            accessToken, accessExpires, rawRefresh, refreshExpires);
    }
}
