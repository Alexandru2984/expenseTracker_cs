using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Integrations;
using ExpenseTracker.Api.Services;
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
        var grant = await FinanceTokens.AuthenticateAsync(Request, db, ct);
        if (grant is null) return Unauthorized();
        var now = DateTime.UtcNow;
        var subscriptions = await db.Subscriptions.AsNoTracking().Where(s => s.UserId == grant.UserId)
            .OrderBy(s => s.Id).Take(5001)
            .Select(s => new { sourceId = s.Id, s.Name, s.Cost, s.Currency, s.BillingPeriod, s.NextBillingDate, s.Category, s.IsActive, s.UpdatedAt })
            .ToListAsync(ct);
        if (subscriptions.Count > 5000) return StatusCode(413, new ProblemDetails { Title = "Integration snapshot exceeds 5000 subscriptions." });
        return Ok(new { schemaVersion = 1, generatedAt = now, tokenExpiresAt = grant.ExpiresAt, subscriptions });
    }

    [HttpGet("{id:guid}/price-history")]
    public async Task<IActionResult> History(Guid id, [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        if (!config.GetValue<bool>("FinanceIntegration:Enabled")) return NotFound();
        var grant = await FinanceTokens.AuthenticateAsync(Request, db, ct);
        if (grant is null) return Unauthorized();
        var history = await PriceHistory.ReadAsync(db, grant.UserId, id, skip, take, ct);
        return history is null ? NotFound() : Ok(new { schemaVersion = 1, generatedAt = DateTime.UtcNow,
            tokenExpiresAt = grant.ExpiresAt, history });
    }
}
