namespace ExpenseTracker.Api.Models;

public class MonthlyBudget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Currency { get; set; } = "RON";
    public string Category { get; set; } = "";
    public string CategoryKey { get; set; } = "";
    public decimal MonthlyLimit { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
