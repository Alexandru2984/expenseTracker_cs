using System.ComponentModel.DataAnnotations;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Dtos;

public class CreateSubscriptionDto : IValidatableObject
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Range(0, 1_000_000)]
    public decimal Cost { get; set; }

    [Required]
    [StringLength(10, MinimumLength = 1)]
    [RegularExpression("^[A-Za-z]{3}$", ErrorMessage = "Valuta trebuie să aibă trei litere (RON, EUR, USD…).")]
    public string Currency { get; set; } = "RON";

    [EnumDataType(typeof(BillingPeriod))]
    public BillingPeriod BillingPeriod { get; set; } = BillingPeriod.Monthly;

    [Required]
    public DateOnly NextBillingDate { get; set; }

    [StringLength(100)]
    public string Category { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (NextBillingDate.Year < 1900 || NextBillingDate.Year > 9998)
            yield return new ValidationResult("Data scadentă este obligatorie (anii 1900–9998).", [nameof(NextBillingDate)]);
        if (decimal.Round(Cost, 2) != Cost)
            yield return new ValidationResult("Costul poate avea maximum două zecimale.", [nameof(Cost)]);
        if ((Name?.Any(char.IsControl) ?? false) || (Category?.Any(char.IsControl) ?? false))
            yield return new ValidationResult("Numele și categoria nu pot conține caractere de control.", [nameof(Name), nameof(Category)]);
    }
}

public class UpdateSubscriptionDto : CreateSubscriptionDto { }

public class SubscriptionResponseDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Cost { get; set; }
    public string Currency { get; set; } = string.Empty;
    public BillingPeriod BillingPeriod { get; set; }
    public DateOnly NextBillingDate { get; set; }
    public string Category { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PagedResult<T>
{
    public IEnumerable<T> Items { get; set; } = Enumerable.Empty<T>();
    public int Total { get; set; }
}

public class SummaryResponseDto
{
    public IEnumerable<CurrencySummary> ByCurrency { get; set; } = Enumerable.Empty<CurrencySummary>();
    public int ActiveSubscriptions { get; set; }
    public int TotalSubscriptions { get; set; }
    public int DueThisWeek { get; set; }
}

public class CurrencySummary
{
    public string Currency { get; set; } = string.Empty;
    public decimal MonthlyTotal { get; set; }
    public decimal YearlyTotal { get; set; }
    public int ActiveCount { get; set; }
}
