using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SubscriptionsController : ControllerBase
{
    private readonly AppDbContext _db;

    public SubscriptionsController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET /api/subscriptions?skip=0&take=50
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        take = Math.Min(take, 200);
        var userId = GetUserId();

        var query = _db.Subscriptions.Where(s => s.UserId == userId).OrderBy(s => s.Name);
        var total = await query.CountAsync();
        var items = await query.Skip(skip).Take(take).ToListAsync();

        return Ok(new PagedResult<SubscriptionResponseDto>
        {
            Items = items.Select(ToDto),
            Total = total
        });
    }

    // GET /api/subscriptions/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var item = await _db.Subscriptions.FindAsync(id);
        if (item is null || item.UserId != GetUserId()) return NotFound();
        return Ok(ToDto(item));
    }

    // POST /api/subscriptions
    [HttpPost]
    [EnableRateLimiting("writes")]
    public async Task<IActionResult> Create([FromBody] CreateSubscriptionDto dto)
    {
        var item = new SubscriptionItem
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            Cost = dto.Cost,
            Currency = dto.Currency,
            BillingPeriod = dto.BillingPeriod,
            NextBillingDate = dto.NextBillingDate,
            Category = dto.Category,
            IsActive = true,
            UserId = GetUserId()
        };

        _db.Subscriptions.Add(item);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, ToDto(item));
    }

    // PUT /api/subscriptions/{id}
    [HttpPut("{id:guid}")]
    [EnableRateLimiting("writes")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSubscriptionDto dto)
    {
        var existing = await _db.Subscriptions.FindAsync(id);
        if (existing is null || existing.UserId != GetUserId()) return NotFound();

        existing.Name = dto.Name;
        existing.Cost = dto.Cost;
        existing.Currency = dto.Currency;
        existing.BillingPeriod = dto.BillingPeriod;
        existing.NextBillingDate = dto.NextBillingDate;
        existing.Category = dto.Category;
        existing.IsActive = dto.IsActive;

        await _db.SaveChangesAsync();
        return Ok(ToDto(existing));
    }

    // DELETE /api/subscriptions/{id}
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting("writes")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var item = await _db.Subscriptions.FindAsync(id);
        if (item is null || item.UserId != GetUserId()) return NotFound();

        _db.Subscriptions.Remove(item);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // GET /api/subscriptions/summary
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        var userId = GetUserId();
        var all = await _db.Subscriptions.Where(s => s.UserId == userId).ToListAsync();
        var active = all.Where(s => s.IsActive).ToList();

        var byCurrency = active
            .GroupBy(s => s.Currency)
            .Select(g =>
            {
                var monthly = g.Sum(s =>
                    s.BillingPeriod == BillingPeriod.Yearly ? s.Cost / 12m : s.Cost);
                return new CurrencySummary
                {
                    Currency = g.Key,
                    MonthlyTotal = Math.Round(monthly, 2),
                    YearlyTotal = Math.Round(monthly * 12, 2),
                    ActiveCount = g.Count()
                };
            })
            .OrderBy(x => x.Currency);

        return Ok(new SummaryResponseDto
        {
            ByCurrency = byCurrency,
            ActiveSubscriptions = active.Count,
            TotalSubscriptions = all.Count
        });
    }

    private static SubscriptionResponseDto ToDto(SubscriptionItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Cost = item.Cost,
        Currency = item.Currency,
        BillingPeriod = item.BillingPeriod,
        NextBillingDate = item.NextBillingDate,
        Category = item.Category,
        IsActive = item.IsActive,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt
    };
}

