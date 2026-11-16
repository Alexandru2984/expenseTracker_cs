using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Controllers;

[ApiController, Authorize, Route("api/goals")]
public sealed class GoalsController(AppDbContext db, VerificationService verification) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    private ConflictObjectResult Changed() => Conflict(new ProblemDetails
        { Title = "Obiectivul a fost modificat între timp. Reîncarcă obiectivele înainte de a continua." });

    private async Task<bool> LockAccount(CancellationToken ct)
    {
        var user = await db.Users.FindAsync(new object[] { UserId }, ct);
        if (user is null) return false;
        try { await verification.LockAccountAsync(user); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
    private static void Apply(SavingsGoal goal, SaveGoalDto request)
    {
        goal.Name = request.Name.Trim(); goal.Description = request.Description?.Trim() ?? "";
        goal.Currency = request.Currency.ToUpperInvariant(); goal.TargetAmount = request.TargetAmount;
        goal.MonthlyContribution = request.MonthlyContribution; goal.TargetDate = request.TargetDate;
        goal.IsArchived = request.IsArchived;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var userId = UserId;
        // One SQL snapshot: versions and aggregates cannot come from different writes.
        var rows = await db.SavingsGoals.AsNoTracking().Where(g => g.UserId == userId)
            .OrderBy(g => g.IsArchived).ThenByDescending(g => g.CreatedAt).ThenBy(g => g.Id).Take(50)
            .Select(g => new { Goal = g, Saved = g.Contributions.Sum(c => (decimal?)c.Amount) ?? 0,
                Count = g.Contributions.Count() }).ToListAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return Ok(rows.Select(r => GoalProgress.Calculate(r.Goal, r.Saved, r.Count, today)));
    }

    [HttpPost, EnableRateLimiting("writes")]
    public async Task<IActionResult> Create(SaveGoalDto request, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (!await LockAccount(ct)) return Changed();
        if (await db.SavingsGoals.CountAsync(g => g.UserId == UserId, ct) >= 50)
            return Conflict(new ProblemDetails { Title = "Poți păstra maximum 50 de obiective, inclusiv cele arhivate." });
        var goal = new SavingsGoal { UserId = UserId };
        Apply(goal, request); db.SavingsGoals.Add(goal); await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return StatusCode(201, new { goal.Id, goal.Version });
    }

    [HttpPut("{id:guid}"), EnableRateLimiting("writes")]
    public async Task<IActionResult> Update(Guid id, UpdateGoalDto request, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (!await LockAccount(ct)) return Changed();
        var goal = await db.SavingsGoals.SingleOrDefaultAsync(g => g.Id == id && g.UserId == UserId, ct);
        if (goal is null) return NotFound();
        if (request.Version != goal.Version) return Changed();
        if (!goal.Currency.Equals(request.Currency, StringComparison.OrdinalIgnoreCase)
            && await db.GoalContributions.AnyAsync(c => c.GoalId == id, ct))
            return Conflict(new ProblemDetails { Title = "Valuta nu poate fi schimbată cât timp obiectivul are contribuții." });
        Apply(goal, request);
        // Even saving identical fields advances the concurrency boundary.
        db.Entry(goal).Property(g => g.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Ok(new { goal.Id, goal.Version });
    }

    [HttpDelete("{id:guid}"), EnableRateLimiting("writes")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery, Required] Guid? version, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (!await LockAccount(ct)) return Changed();
        var goal = await db.SavingsGoals.SingleOrDefaultAsync(g => g.Id == id && g.UserId == UserId, ct);
        if (goal is null) return NotFound();
        if (version != goal.Version) return Changed();
        db.SavingsGoals.Remove(goal); await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/contributions")]
    public async Task<IActionResult> History(Guid id, [FromQuery, Range(0, 5000)] int skip = 0,
        [FromQuery, Range(1, 100)] int take = 20, CancellationToken ct = default)
    {
        if (!await db.SavingsGoals.AnyAsync(g => g.Id == id && g.UserId == UserId, ct)) return NotFound();
        var query = db.GoalContributions.AsNoTracking().Where(c => c.GoalId == id && c.Goal.UserId == UserId);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(c => c.ContributedOn).ThenByDescending(c => c.CreatedAt)
            .ThenBy(c => c.Id).Skip(skip).Take(take)
            .Select(c => new GoalContributionDto(c.Id, c.Amount, c.ContributedOn, c.Note, c.CreatedAt)).ToListAsync(ct);
        return Ok(new { total, skip, take, items });
    }

    [HttpPost("{id:guid}/contributions"), EnableRateLimiting("writes")]
    public async Task<IActionResult> Contribute(Guid id, AddGoalContributionDto request, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (!await LockAccount(ct)) return Changed();
        var goal = await db.SavingsGoals.SingleOrDefaultAsync(g => g.Id == id && g.UserId == UserId, ct);
        if (goal is null) return NotFound();
        var note = request.Note?.Trim() ?? "";
        var existing = await db.GoalContributions.SingleOrDefaultAsync(c => c.GoalId == id && c.Id == request.Id, ct);
        if (existing is not null)
        {
            if (existing.Amount != request.Amount || existing.ContributedOn != request.ContributedOn || existing.Note != note)
                return Conflict(new ProblemDetails { Title = "Identitatea contribuției a fost folosită pentru alte date. Reîncarcă istoricul." });
            // Retry after a lost response succeeds, even if its original version is old.
            return Ok(new { existing.Id, goal.Version });
        }
        if (request.Version != goal.Version) return Changed();
        if (goal.IsArchived) return Conflict(new ProblemDetails { Title = "Reactivează obiectivul înainte de a adăuga o contribuție." });
        if (await db.GoalContributions.CountAsync(c => c.Goal.UserId == UserId, ct) >= 5000)
            return Conflict(new ProblemDetails { Title = "Ai atins limita de 5000 de contribuții. Elimină înregistrările de care nu mai ai nevoie." });
        var saved = await db.GoalContributions.Where(c => c.GoalId == id).SumAsync(c => (decimal?)c.Amount, ct) ?? 0;
        if (saved + request.Amount > 1000000m)
            return Conflict(new ProblemDetails { Title = "Totalul înregistrat într-un obiectiv poate avea maximum 1.000.000." });
        var entry = new GoalContribution { Id = request.Id!.Value, GoalId = id, Amount = request.Amount,
            ContributedOn = request.ContributedOn!.Value, Note = note };
        db.GoalContributions.Add(entry);
        db.Entry(goal).Property(g => g.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return StatusCode(201, new { entry.Id, goal.Version });
    }

    [HttpDelete("{id:guid}/contributions/{contributionId:guid}"), EnableRateLimiting("writes")]
    public async Task<IActionResult> RemoveContribution(Guid id, Guid contributionId,
        [FromQuery, Required] Guid? version, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (!await LockAccount(ct)) return Changed();
        var goal = await db.SavingsGoals.SingleOrDefaultAsync(g => g.Id == id && g.UserId == UserId, ct);
        if (goal is null) return NotFound();
        if (version != goal.Version) return Changed();
        var entry = await db.GoalContributions.SingleOrDefaultAsync(c => c.GoalId == id && c.Id == contributionId, ct);
        if (entry is null) return NotFound();
        db.GoalContributions.Remove(entry);
        db.Entry(goal).Property(g => g.UpdatedAt).IsModified = true;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return NoContent();
    }
}
