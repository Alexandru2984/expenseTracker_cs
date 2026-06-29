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
public class SubscriptionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CurrencyService _currencyService;

    public SubscriptionsController(AppDbContext db, CurrencyService currencyService)
    {
        _db = db;
        _currencyService = currencyService;
    }

    // Never throws: a malformed/absent subject claim resolves to Guid.Empty,
    // which simply matches no rows instead of producing a 500.
    private Guid GetUserId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    // GET /api/subscriptions?skip=0&take=50&search=xyz&sortBy=cost&sortDesc=true
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int skip = 0, 
        [FromQuery] int take = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = "name",
        [FromQuery] bool sortDesc = false)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var userId = GetUserId();

        var query = _db.Subscriptions.Where(s => s.UserId == userId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(s => s.Name.ToLower().Contains(searchLower) || 
                                     s.Category.ToLower().Contains(searchLower));
        }

        query = sortBy?.ToLower() switch
        {
            "cost" => sortDesc ? query.OrderByDescending(s => s.Cost) : query.OrderBy(s => s.Cost),
            "nextbillingdate" => sortDesc ? query.OrderByDescending(s => s.NextBillingDate) : query.OrderBy(s => s.NextBillingDate),
            "category" => sortDesc ? query.OrderByDescending(s => s.Category) : query.OrderBy(s => s.Category),
            "currency" => sortDesc ? query.OrderByDescending(s => s.Currency) : query.OrderBy(s => s.Currency),
            _ => sortDesc ? query.OrderByDescending(s => s.Name) : query.OrderBy(s => s.Name)
        };

        var total = await query.CountAsync();
        var items = await query.Skip(skip).Take(take).ToListAsync();

        return Ok(new PagedResult<SubscriptionResponseDto>
        {
            Items = items.Select(ToDto),
            Total = total
        });
    }

    // GET /api/subscriptions/rates
    [HttpGet("rates")]
    [AllowAnonymous]
    public async Task<IActionResult> GetRates()
    {
        var rates = await _currencyService.GetRatesAsync();
        return Ok(rates);
    }

    // GET /api/subscriptions/export
    [HttpGet("export")]
    public async Task<IActionResult> ExportCsv()
    {
        var userId = GetUserId();
        var items = await _db.Subscriptions.Where(s => s.UserId == userId).OrderBy(s => s.Name).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Id,Name,Cost,Currency,BillingPeriod,NextBillingDate,Category,IsActive,CreatedAt");

        foreach (var item in items)
        {
            var cost = item.Cost.ToString(System.Globalization.CultureInfo.InvariantCulture);
            sb.AppendLine(string.Join(',',
                item.Id,
                CsvField(item.Name),
                cost,
                CsvField(item.Currency),
                item.BillingPeriod,
                item.NextBillingDate.ToString("yyyy-MM-dd"),
                CsvField(item.Category),
                item.IsActive,
                item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        // UTF-8 BOM so Excel opens accented characters correctly
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"abonamente_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
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

    // Quotes a CSV field and neutralizes spreadsheet formula injection
    // (values starting with = + - @ or control chars are prefixed with ').
    private static string CsvField(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
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

