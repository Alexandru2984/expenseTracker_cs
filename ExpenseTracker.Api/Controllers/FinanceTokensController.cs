using System.Security.Claims;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Integrations;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Controllers;

[ApiController, Authorize, Route("api/account/finance-tokens")]
public sealed class FinanceTokensController(AppDbContext db, IConfiguration config) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    private bool Enabled => config.GetValue<bool>("FinanceIntegration:Enabled");

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!Enabled) return Ok(new { available = false, tokens = Array.Empty<object>() });
        var now = DateTime.UtcNow;
        var tokens = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == UserId && t.TokenHash.StartsWith(FinanceTokens.HashNamespace)
                && t.RevokedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.CreatedAt, t.ExpiresAt }).ToListAsync(ct);
        return Ok(new { available = true, tokens });
    }

    [HttpPost, EnableRateLimiting("auth")]
    public async Task<IActionResult> Create(CreateFinanceTokenRequest request, CancellationToken ct)
    {
        if (!Enabled) return NotFound();
        var user = await db.Users.FindAsync(new object[] { UserId }, ct);
        if (user is null) return Unauthorized();
        if (!PasswordRules.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new ProblemDetails { Title = "Parola curentă este incorectă." });
        var now = DateTime.UtcNow;
        var count = await db.RefreshTokens.CountAsync(t => t.UserId == UserId
            && t.TokenHash.StartsWith(FinanceTokens.HashNamespace) && t.RevokedAt == null && t.ExpiresAt > now, ct);
        if (count >= 5) return Conflict(new ProblemDetails { Title = "Revocă un token existent înainte de a crea altul (maximum 5)." });
        var raw = FinanceTokens.Create();
        var row = new RefreshToken { UserId = UserId, TokenHash = FinanceTokens.Hash(raw), ExpiresAt = now.AddDays(request.ValidDays) };
        db.RefreshTokens.Add(row);
        await db.SaveChangesAsync(ct);
        return Ok(new { row.Id, token = raw, row.ExpiresAt }); // raw token is shown once only
    }

    [HttpDelete("{id:guid}"), EnableRateLimiting("writes")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        if (!Enabled) return NotFound();
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.Id == id && t.UserId == UserId
            && t.TokenHash.StartsWith(FinanceTokens.HashNamespace), ct);
        if (token is null) return NotFound();
        token.RevokedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
