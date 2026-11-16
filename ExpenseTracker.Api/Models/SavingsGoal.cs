namespace ExpenseTracker.Api.Models;

public class SavingsGoal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Currency { get; set; } = "RON";
    public decimal TargetAmount { get; set; }
    public decimal MonthlyContribution { get; set; }
    public DateOnly? TargetDate { get; set; }
    public bool IsArchived { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<GoalContribution> Contributions { get; set; } = new List<GoalContribution>();
}

public class GoalContribution
{
    // Client-generated idempotency key, scoped to this goal.
    public Guid Id { get; set; }
    public Guid GoalId { get; set; }
    public SavingsGoal Goal { get; set; } = null!;
    public decimal Amount { get; set; }
    public DateOnly ContributedOn { get; set; }
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
