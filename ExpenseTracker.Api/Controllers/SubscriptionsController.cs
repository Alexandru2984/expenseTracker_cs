using System.Globalization;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Infrastructure;
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
        [FromQuery] bool sortDesc = false,
        [FromQuery] string? status = null)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var userId = GetUserId();

        var query = _db.Subscriptions.Where(s => s.UserId == userId);

        query = status?.ToLower() switch
        {
            "active" => query.Where(s => s.IsActive),
            "inactive" => query.Where(s => !s.IsActive),
            _ => query
        };

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
    [HttpGet("rates/status"), AllowAnonymous]
    public async Task<IActionResult> GetRatesStatus() => Ok(await _currencyService.GetSnapshotAsync());

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
                Csv.Field(item.Name),
                cost,
                Csv.Field(item.Currency),
                item.BillingPeriod,
                item.NextBillingDate.ToString("yyyy-MM-dd"),
                Csv.Field(item.Category),
                item.IsActive,
                item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        // UTF-8 BOM so Excel opens accented characters correctly
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv", $"abonamente_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
    }

    // POST /api/subscriptions/import — bulk import from a CSV (same shape as export)
    [HttpPost("import")]
    [EnableRateLimiting("writes")]
    public async Task<IActionResult> ImportCsv(IFormFile? file)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new ProblemDetails { Title = "Fișier lipsă.", Detail = "Încarcă un fișier CSV." });
        if (file.Length > 1_000_000)
            return BadRequest(new ProblemDetails { Title = "Fișier prea mare.", Detail = "Dimensiune maximă 1 MB." });

        var userId = GetUserId();

        string content;
        using (var reader = new StreamReader(file.OpenReadStream()))
            content = await reader.ReadToEndAsync();

        List<List<string>> rows;
        try { rows = Csv.Parse(content); }
        catch (FormatException ex)
        {
            return BadRequest(new ProblemDetails { Title = "CSV invalid.", Detail = ex.Message });
        }
        if (rows.Count == 0)
            return BadRequest(new ProblemDetails { Title = "CSV gol." });

        var header = rows[0].Select(h => h.Trim().TrimStart('\uFEFF').ToLowerInvariant()).ToList();
        if (header.Distinct().Count() != header.Count)
            return BadRequest(new ProblemDetails { Title = "Antet CSV cu coloane duplicate." });
        int Col(string name) => header.IndexOf(name);
        int iName = Col("name"), iCost = Col("cost"), iCur = Col("currency"),
            iPeriod = Col("billingperiod"), iDate = Col("nextbillingdate"),
            iCat = Col("category"), iActive = Col("isactive");

        if (iName < 0 || iCost < 0 || iDate < 0)
            return BadRequest(new ProblemDetails
            {
                Title = "Antet CSV invalid.",
                Detail = "Sunt necesare cel puțin coloanele Name, Cost și NextBillingDate."
            });

        var existing = (await _db.Subscriptions
                .Where(s => s.UserId == userId)
                .Select(s => s.Name)
                .ToListAsync())
            .Select(n => n.ToLowerInvariant())
            .ToHashSet();

        int imported = 0, skipped = 0, rejected = 0;
        var errors = new List<string>();
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            string Get(int idx) => idx >= 0 && idx < row.Count ? row[idx].Trim() : "";

            var name = Get(iName);
            void Reject(string message) { rejected++; if (errors.Count < 10) errors.Add($"Rândul {r + 1}: {message}"); }
            if (!decimal.TryParse(Get(iCost), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var cost))
            { Reject("cost invalid (separator zecimal: punct)."); continue; }
            if (!DateOnly.TryParseExact(Get(iDate), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            { Reject("dată invalidă (folosește YYYY-MM-DD)."); continue; }
            var period = BillingPeriod.Monthly;
            if (iPeriod >= 0 && (!Enum.TryParse(Get(iPeriod), true, out period) || !Enum.IsDefined(period)))
            { Reject("frecvență invalidă (Monthly sau Yearly)."); continue; }
            var isActive = true;
            if (iActive >= 0 && !bool.TryParse(Get(iActive), out isActive))
            { Reject("IsActive trebuie să fie true sau false."); continue; }
            var dto = new CreateSubscriptionDto { Name = name, Cost = cost,
                Currency = iCur >= 0 ? Get(iCur).ToUpperInvariant() : "RON", BillingPeriod = period,
                NextBillingDate = date, Category = iCat >= 0 ? Get(iCat) : "", IsActive = isActive };
            var validation = new List<ValidationResult>();
            if (!Validator.TryValidateObject(dto, new ValidationContext(dto), validation, true))
            { Reject(string.Join(" ", validation.Select(v => v.ErrorMessage))); continue; }
            if (existing.Contains(name.ToLowerInvariant())) { skipped++; continue; }

            _db.Subscriptions.Add(new SubscriptionItem
            {
                Id = Guid.NewGuid(),
                Name = name,
                Cost = cost,
                Currency = dto.Currency,
                BillingPeriod = period,
                NextBillingDate = date,
                Category = dto.Category,
                IsActive = isActive,
                UserId = userId
            });
            existing.Add(name.ToLowerInvariant());
            imported++;
        }

        if (imported > 0) await _db.SaveChangesAsync();

        return Ok(new { imported, skipped, rejected, errors });
    }

    // GET /api/subscriptions/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var item = await _db.Subscriptions.FindAsync(id);
        if (item is null || item.UserId != GetUserId()) return NotFound();
        return Ok(ToDto(item));
    }

    // GET /api/subscriptions/{id}/price-history
    [HttpGet("{id:guid}/price-history")]
    public async Task<IActionResult> GetPriceHistory(Guid id, [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        var history = await PriceHistory.ReadAsync(_db, GetUserId(), id, skip, take, ct);
        return history is null ? NotFound() : Ok(history);
    }

    // POST /api/subscriptions
    [HttpPost]
    [EnableRateLimiting("writes")]
    public async Task<IActionResult> Create([FromBody] CreateSubscriptionDto dto)
    {
        var item = new SubscriptionItem
        {
            Id = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            Cost = dto.Cost,
            Currency = dto.Currency.Trim().ToUpperInvariant(),
            BillingPeriod = dto.BillingPeriod,
            NextBillingDate = dto.NextBillingDate,
            Category = dto.Category?.Trim() ?? "",
            IsActive = dto.IsActive,
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
        if (dto.ExpectedUpdatedAt is { } expected && expected.UtcDateTime.Ticks / 10 != existing.UpdatedAt.Ticks / 10)
            return Conflict(new ProblemDetails { Title = "Abonamentul a fost modificat între timp.",
                Detail = "Reîncarcă lista și redeschide abonamentul actual înainte de salvare. Datele introduse au fost păstrate." });

        existing.Name = dto.Name.Trim();
        existing.Cost = dto.Cost;
        existing.Currency = dto.Currency.Trim().ToUpperInvariant();
        existing.BillingPeriod = dto.BillingPeriod;
        existing.NextBillingDate = dto.NextBillingDate;
        existing.Category = dto.Category?.Trim() ?? "";
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
    [HttpGet("insights")]
    public async Task<IActionResult> GetInsights(CancellationToken ct)
    {
        var userId = GetUserId();
        var items = await _db.Subscriptions.AsNoTracking().Where(s => s.UserId == userId).Take(5001).ToListAsync(ct);
        if (items.Count > 5000) return StatusCode(413, new ProblemDetails { Title = "Analiza acceptă maximum 5000 de abonamente." });
        return Ok(SubscriptionInsights.Build(items, DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [HttpGet("calendar")]
    public async Task<IActionResult> ExportCalendar(CancellationToken ct)
    {
        var userId = GetUserId();
        var items = await _db.Subscriptions.AsNoTracking().Where(s => s.UserId == userId && s.IsActive)
            .OrderBy(s => s.NextBillingDate).ThenBy(s => s.Id).Take(5001).ToListAsync(ct);
        if (items.Count > 5000) return StatusCode(413, new ProblemDetails { Title = "Calendarul acceptă maximum 5000 de abonamente." });
        return File(CalendarExport.Build(items, DateTime.UtcNow), "text/calendar; charset=utf-8", "abonamente.ics");
    }

    [HttpPost("{id:guid}/status"), EnableRateLimiting("writes")]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SubscriptionStatusDto dto, CancellationToken ct)
    {
        var userId = GetUserId();
        var item = await _db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (item is null) return NotFound();
        item.IsActive = dto.IsActive!.Value;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(item));
    }

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

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dueThisWeek = active.Count(s => s.NextBillingDate >= today && s.NextBillingDate <= today.AddDays(7));

        return Ok(new SummaryResponseDto
        {
            ByCurrency = byCurrency,
            ActiveSubscriptions = active.Count,
            TotalSubscriptions = all.Count,
            DueThisWeek = dueThisWeek
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

