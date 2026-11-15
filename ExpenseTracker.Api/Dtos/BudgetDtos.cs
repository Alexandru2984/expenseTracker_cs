using System.ComponentModel.DataAnnotations;

namespace ExpenseTracker.Api.Dtos;

public class CreateBudgetDto : IValidatableObject
{
    [Required, RegularExpression("^[A-Za-z]{3}$")]
    public string Currency { get; set; } = "RON";
    [StringLength(100)]
    public string Category { get; set; } = "";
    [Range(typeof(decimal), "0.01", "1000000", ParseLimitsInInvariantCulture = true)]
    public decimal MonthlyLimit { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (decimal.Round(MonthlyLimit, 2) != MonthlyLimit)
            yield return new ValidationResult("Limita poate avea maximum două zecimale.", [nameof(MonthlyLimit)]);
        if (Category?.Any(char.IsControl) == true)
            yield return new ValidationResult("Categoria nu poate conține caractere de control.", [nameof(Category)]);
    }
}

public class UpdateBudgetDto : CreateBudgetDto
{
    [Required]
    public Guid? Version { get; set; }
}

public sealed record BudgetResponseDto(Guid Id, string Currency, string Category, decimal MonthlyLimit,
    decimal MonthlyPlanned, decimal Remaining, decimal UsagePercent, bool IsOverBudget, Guid Version, DateTime UpdatedAt);
