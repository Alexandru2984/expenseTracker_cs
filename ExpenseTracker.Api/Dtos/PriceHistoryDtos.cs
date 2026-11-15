using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Dtos;

public sealed record PricePointDto(decimal Cost, string Currency, BillingPeriod BillingPeriod);
public sealed record PriceChangeDto(Guid Id, DateTime ChangedAt, PricePointDto Previous, PricePointDto Current,
    decimal? MonthlyDelta, decimal? ChangePercent);
public sealed record PriceHistoryDto(Guid SubscriptionId, PricePointDto Current, int Total, int Skip, int Take,
    IReadOnlyList<PriceChangeDto> Entries);
