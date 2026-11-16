using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Services;

public static class GoalProgress
{
    public static GoalResponseDto Calculate(SavingsGoal goal, decimal saved, int count, DateOnly today)
    {
        var remaining = Math.Max(0, goal.TargetAmount - saved);
        var months = remaining == 0 ? 0 : goal.MonthlyContribution > 0
            ? (int?)decimal.Ceiling(remaining / goal.MonthlyContribution) : null;
        // Keep the calendar projection bounded, even for a one-cent monthly plan.
        var estimated = months is >= 0 and <= 1200 ? today.AddMonths(months.Value) : (DateOnly?)null;
        decimal? required = null;
        int? periods = null;
        if (goal.TargetDate is { } due && due >= today)
        {
            periods = (due.Year - today.Year) * 12 + due.Month - today.Month;
            if (today.AddMonths(periods.Value) > due) periods--;
            // Count only contributions that can occur on/before the deadline.
            required = remaining == 0 ? 0 : periods > 0
                ? decimal.Ceiling(remaining / periods.Value * 100m) / 100m : null;
        }
        var status = goal.IsArchived ? "archived" : remaining == 0 ? "completed"
            : goal.TargetDate < today ? "overdue"
            : periods == 0 ? "due-soon"
            : goal.TargetDate is not null && (estimated is null || estimated > goal.TargetDate) ? "behind"
            : goal.MonthlyContribution > 0 ? "on-track" : "no-plan";
        return new(goal.Id, goal.Name, goal.Description, goal.Currency, goal.TargetAmount,
            goal.MonthlyContribution, goal.TargetDate, goal.IsArchived, goal.Version, goal.CreatedAt,
            goal.UpdatedAt, saved, remaining, Math.Min(100m, decimal.Round(saved / goal.TargetAmount * 100m, 1)),
            count, status, months, estimated, required, today);
    }
}
