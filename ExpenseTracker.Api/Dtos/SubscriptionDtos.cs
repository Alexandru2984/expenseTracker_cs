using System.ComponentModel.DataAnnotations;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Infrastructure;

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
    public SubscriptionDetailsDto? Details { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (NextBillingDate.Year < 1900 || NextBillingDate.Year > 9998)
            yield return new ValidationResult("Data scadentă este obligatorie (anii 1900–9998).", [nameof(NextBillingDate)]);
        if (decimal.Round(Cost, 2) != Cost)
            yield return new ValidationResult("Costul poate avea maximum două zecimale.", [nameof(Cost)]);
        if ((Name?.Any(char.IsControl) ?? false) || (Category?.Any(char.IsControl) ?? false))
            yield return new ValidationResult("Numele și categoria nu pot conține caractere de control.", [nameof(Name), nameof(Category)]);
        if (Details is not null)
        {
            var errors = new List<ValidationResult>();
            Validator.TryValidateObject(Details, new ValidationContext(Details), errors, true);
            foreach (var error in errors)
                yield return new ValidationResult(error.ErrorMessage, error.MemberNames.Select(m => $"Details.{m}"));
            if (Details.TrialEndsOn > NextBillingDate)
                yield return new ValidationResult("Prima scadență nu poate fi înaintea finalului perioadei de probă.", [nameof(NextBillingDate)]);
        }
    }
}

public class SubscriptionDetailsDto : IValidatableObject
{
    [StringLength(2000)]
    public string? Notes { get; set; } = "";
    [StringLength(2048)]
    public string? ProviderUrl { get; set; } = "";
    public DateOnly? TrialEndsOn { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Notes?.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) == true)
            yield return new ValidationResult("Notele conțin caractere de control nepermise.", [nameof(Notes)]);
        if (!ProviderLinks.TryNormalize(ProviderUrl, out _))
            yield return new ValidationResult("Linkul furnizorului trebuie să fie un URL HTTP/HTTPS absolut, fără credențiale sau spații necodificate.", [nameof(ProviderUrl)]);
        if (TrialEndsOn is { Year: < 1900 or > 9998 })
            yield return new ValidationResult("Finalul perioadei de probă trebuie să fie în anii 1900–9998.", [nameof(TrialEndsOn)]);
    }

    public void ApplyTo(SubscriptionItem item)
    {
        if (!ProviderLinks.TryNormalize(ProviderUrl, out var url)) throw new ArgumentException("Invalid provider link.");
        item.Notes = (Notes ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        item.ProviderUrl = url;
        item.TrialEndsOn = TrialEndsOn;
    }
}

public class UpdateSubscriptionDto : CreateSubscriptionDto
{
    // Optional for existing API clients. The web UI always sends the version it opened.
    public DateTimeOffset? ExpectedUpdatedAt { get; set; }
}

public class SubscriptionStatusDto
{
    [Required]
    public bool? IsActive { get; set; }
}

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
    public SubscriptionDetailsDto Details { get; set; } = new();
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
