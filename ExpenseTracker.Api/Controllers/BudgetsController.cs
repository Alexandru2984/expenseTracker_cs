using System.Security.Claims;
using System.Text;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ExpenseTracker.Api.Controllers;

[ApiController, Authorize, Route("api/budgets")]
public sealed class BudgetsController(AppDbContext db, VerificationService verification) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    private static string Key(string? value) => (value ?? "").Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    private static bool Duplicate(DbUpdateException ex) => ex.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_Budgets_UserId_Currency_CategoryKey" };
    private ConflictObjectResult Changed() => Conflict(new ProblemDetails
        { Title = "Bugetul a fost modificat între timp. Încarcă versiunea curentă înainte de a salva." });

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var userId = UserId;
        var budgets = await db.Budgets.AsNoTracking().Where(b => b.UserId == userId)
            .OrderBy(b => b.Currency).ThenBy(b => b.Category).ToListAsync(ct);
        var items = await db.Subscriptions.AsNoTracking().Where(s => s.UserId == userId && s.IsActive)
            .Take(5001).ToListAsync(ct);
        if (items.Count > 5000) return StatusCode(413, new ProblemDetails { Title = "Analiza bugetelor acceptă maximum 5000 de abonamente active." });
        return Ok(budgets.Select(b =>
        {
            var planned = Math.Round(items.Where(s => s.Currency.Equals(b.Currency, StringComparison.OrdinalIgnoreCase)
                && (b.CategoryKey.Length == 0 || Key(string.IsNullOrWhiteSpace(s.Category) ? "Altele" : s.Category) == b.CategoryKey))
                .Sum(SubscriptionInsights.MonthlyCost), 2);
            return ToDto(b, planned);
        }));
    }

    [HttpPost, EnableRateLimiting("writes")]
    public async Task<IActionResult> Create(CreateBudgetDto request, CancellationToken ct)
    {
        var userId = UserId;
        var user = await db.Users.FindAsync(new object[] { userId }, ct);
        if (user is null) return Unauthorized();
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        await verification.LockAccountAsync(user);
        if (await db.Budgets.CountAsync(b => b.UserId == userId, ct) >= 50)
            return Conflict(new ProblemDetails { Title = "Poți păstra maximum 50 de bugete. Elimină unul înainte de a crea altul." });
        var currency = request.Currency.ToUpperInvariant(); var key = Key(request.Category);
        if (await db.Budgets.AnyAsync(b => b.UserId == userId && b.Currency == currency && b.CategoryKey == key, ct))
            return Conflict(new ProblemDetails { Title = "Există deja un buget pentru această valută și categorie." });
        var budget = new MonthlyBudget { UserId = userId, Currency = currency, Category = request.Category?.Trim() ?? "",
            CategoryKey = key, MonthlyLimit = request.MonthlyLimit };
        db.Budgets.Add(budget);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Duplicate(ex))
        { return Conflict(new ProblemDetails { Title = "Există deja un buget pentru această valută și categorie." }); }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return StatusCode(201, new { budget.Id, budget.Version });
    }

    [HttpPut("{id:guid}"), EnableRateLimiting("writes")]
    public async Task<IActionResult> Update(Guid id, UpdateBudgetDto request, CancellationToken ct)
    {
        var userId = UserId;
        var budget = await db.Budgets.SingleOrDefaultAsync(b => b.Id == id && b.UserId == userId, ct);
        if (budget is null) return NotFound();
        if (request.Version != budget.Version) return Changed();
        budget.Currency = request.Currency.ToUpperInvariant(); budget.Category = request.Category?.Trim() ?? "";
        budget.CategoryKey = Key(request.Category); budget.MonthlyLimit = request.MonthlyLimit;
        if (await db.Budgets.AnyAsync(b => b.UserId == userId && b.Id != id
            && b.Currency == budget.Currency && b.CategoryKey == budget.CategoryKey, ct))
            return Conflict(new ProblemDetails { Title = "Există deja un buget pentru această valută și categorie." });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        catch (DbUpdateException ex) when (Duplicate(ex))
        { return Conflict(new ProblemDetails { Title = "Există deja un buget pentru această valută și categorie." }); }
        return Ok(new { budget.Id, budget.Version });
    }

    [HttpDelete("{id:guid}"), EnableRateLimiting("writes")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] Guid? version, CancellationToken ct)
    {
        if (version is null) return BadRequest(new ProblemDetails { Title = "Versiunea bugetului este obligatorie." });
        var userId = UserId;
        var budget = await db.Budgets.SingleOrDefaultAsync(b => b.Id == id && b.UserId == userId, ct);
        if (budget is null) return NotFound();
        if (version != budget.Version) return Changed();
        db.Budgets.Remove(budget);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Changed(); }
        return NoContent();
    }

    private static BudgetResponseDto ToDto(MonthlyBudget budget, decimal planned) =>
        new(budget.Id, budget.Currency, budget.Category, budget.MonthlyLimit, planned, budget.MonthlyLimit - planned,
            Math.Round(planned / budget.MonthlyLimit * 100m, 1), planned > budget.MonthlyLimit, budget.Version, budget.UpdatedAt);
}
