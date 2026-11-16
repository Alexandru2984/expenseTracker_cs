using System.ComponentModel.DataAnnotations;

namespace ExpenseTracker.Api.Dtos;

public class SaveGoalDto : IValidatableObject
{
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [StringLength(500)] public string Description { get; set; } = "";
    [Required, RegularExpression("^[A-Za-z]{3}$")] public string Currency { get; set; } = "RON";
    [Range(typeof(decimal), "0.01", "1000000", ParseLimitsInInvariantCulture = true)] public decimal TargetAmount { get; set; }
    [Range(typeof(decimal), "0", "1000000", ParseLimitsInInvariantCulture = true)] public decimal MonthlyContribution { get; set; }
    public DateOnly? TargetDate { get; set; }
    public bool IsArchived { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Any(char.IsControl))
            yield return new ValidationResult("Numele obiectivului este obligatoriu și nu poate conține caractere de control.", [nameof(Name)]);
        if (Description?.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t') == true)
            yield return new ValidationResult("Descrierea conține caractere nepermise.", [nameof(Description)]);
        if (decimal.Round(TargetAmount, 2) != TargetAmount || decimal.Round(MonthlyContribution, 2) != MonthlyContribution)
            yield return new ValidationResult("Sumele pot avea maximum două zecimale.", [nameof(TargetAmount), nameof(MonthlyContribution)]);
        if (TargetDate is { Year: < 1900 or > 2100 })
            yield return new ValidationResult("Termenul trebuie să fie între anii 1900 și 2100.", [nameof(TargetDate)]);
    }
}

public class UpdateGoalDto : SaveGoalDto
{
    [Required] public Guid? Version { get; set; }
}

public class AddGoalContributionDto : IValidatableObject
{
    [Required] public Guid? Id { get; set; }
    [Required] public Guid? Version { get; set; }
    [Range(typeof(decimal), "0.01", "1000000", ParseLimitsInInvariantCulture = true)] public decimal Amount { get; set; }
    [Required] public DateOnly? ContributedOn { get; set; }
    [StringLength(200)] public string Note { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Id == Guid.Empty)
            yield return new ValidationResult("Identitatea contribuției este invalidă.", [nameof(Id)]);
        if (decimal.Round(Amount, 2) != Amount)
            yield return new ValidationResult("Suma poate avea maximum două zecimale.", [nameof(Amount)]);
        if (ContributedOn is { } date && (date.Year < 1900 || date > DateOnly.FromDateTime(DateTime.UtcNow)))
            yield return new ValidationResult("Data contribuției trebuie să fie între 1900 și ziua curentă UTC.", [nameof(ContributedOn)]);
        if (Note?.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t') == true)
            yield return new ValidationResult("Nota conține caractere nepermise.", [nameof(Note)]);
    }
}

public sealed record GoalResponseDto(Guid Id, string Name, string Description, string Currency,
    decimal TargetAmount, decimal MonthlyContribution, DateOnly? TargetDate, bool IsArchived, Guid Version,
    DateTime CreatedAt, DateTime UpdatedAt, decimal SavedAmount, decimal Remaining, decimal ProgressPercent,
    int ContributionCount, string Status, int? MonthsToGoal, DateOnly? EstimatedCompletionDate,
    decimal? RequiredMonthlyContribution, DateOnly AsOfDate);
public sealed record GoalContributionDto(Guid Id, decimal Amount, DateOnly ContributedOn, string Note, DateTime CreatedAt);
