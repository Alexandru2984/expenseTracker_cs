using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Controllers;

/// <summary>A dedicated read-only capability; cookies and normal access/refresh tokens cannot authorize this route.</summary>
[ApiController, AllowAnonymous, Route("api/integrations/finance/subscriptions")]
public sealed class FinanceExportController(AppDbContext db, IConfiguration config) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Snapshot(CancellationToken ct)
    {
        if (!config.GetValue<bool>("FinanceIntegration:Enabled")) return NotFound();
        var authorization = Request.Headers.Authorization;
        if (authorization.Count != 1) return Unauthorized();
        var header = authorization[0] ?? "";
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Unauthorized();
        var token = header[7..];
        if (!FinanceTokens.IsValidFormat(token)) return Unauthorized();
        var hash = FinanceTokens.Hash(token);
        var now = DateTime.UtcNow;
        var grant = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now)
            .Select(t => new { t.UserId, t.ExpiresAt }).SingleOrDefaultAsync(ct);
        if (grant is null || !await db.Users.AnyAsync(u => u.Id == grant.UserId, ct)) return Unauthorized();
        var subscriptions = await db.Subscriptions.AsNoTracking().Where(s => s.UserId == grant.UserId)
            .OrderBy(s => s.Id).Take(5001)
            .Select(s => new { sourceId = s.Id, s.Name, s.Cost, s.Currency, s.BillingPeriod, s.NextBillingDate, s.Category, s.IsActive, s.UpdatedAt })
            .ToListAsync(ct);
        if (subscriptions.Count > 5000) return StatusCode(413, new ProblemDetails { Title = "Integration snapshot exceeds 5000 subscriptions." });
        return Ok(new { schemaVersion = 1, generatedAt = now, tokenExpiresAt = grant.ExpiresAt, subscriptions });
    }
}
