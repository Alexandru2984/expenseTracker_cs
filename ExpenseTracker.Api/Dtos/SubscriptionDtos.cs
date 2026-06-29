using System.ComponentModel.DataAnnotations;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Dtos;

public class CreateSubscriptionDto
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Range(0, 1_000_000)]
    public decimal Cost { get; set; }

    [Required]
    [StringLength(10, MinimumLength = 1)]
    public string Currency { get; set; } = "RON";

    public BillingPeriod BillingPeriod { get; set; } = BillingPeriod.Monthly;

    [Required]
    public DateOnly NextBillingDate { get; set; }

    [StringLength(100)]
    public string Category { get; set; } = string.Empty;
}

public class UpdateSubscriptionDto
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Range(0, 1_000_000)]
    public decimal Cost { get; set; }

    [Required]
    [StringLength(10, MinimumLength = 1)]
    public string Currency { get; set; } = "RON";

    public BillingPeriod BillingPeriod { get; set; } = BillingPeriod.Monthly;

    [Required]
    public DateOnly NextBillingDate { get; set; }

    [StringLength(100)]
    public string Category { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
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
