using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace ExpenseTracker.Api.Services;

public sealed record CurrencySnapshot(Dictionary<string, decimal> Rates, string Source, DateTime? FetchedAt);

public class CurrencyService(IHttpClientFactory clients, IMemoryCache cache, ILogger<CurrencyService> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const string CacheKey = "CurrencySnapshot:v2";

    // Preserve the original endpoint's dictionary contract.
    public async Task<Dictionary<string, decimal>> GetRatesAsync() => (await GetSnapshotAsync()).Rates;

    public async Task<CurrencySnapshot> GetSnapshotAsync()
    {
        if (cache.TryGetValue(CacheKey, out CurrencySnapshot? cached) && cached is not null) return cached;
        await Gate.WaitAsync();
        try
        {
            if (cache.TryGetValue(CacheKey, out cached) && cached is not null) return cached;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var client = clients.CreateClient("CurrencyRates");
                using var response = await client.GetAsync("https://open.er-api.com/v6/latest/RON", cts.Token);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in doc.RootElement.GetProperty("rates").EnumerateObject())
                {
                    if (property.Name.Length != 3 || !property.Name.All(char.IsAsciiLetterUpper)
                        || !property.Value.TryGetDecimal(out var rate) || rate <= 0 || rate > 1_000_000_000m)
                        throw new JsonException("Invalid exchange rate.");
                    rates[property.Name] = rate;
                }
                if (!rates.TryGetValue("RON", out var ron) || ron != 1m || rates.Count < 2)
                    throw new JsonException("Missing RON base rate.");
                cached = new(rates, "live", DateTime.UtcNow);
                cache.Set(CacheKey, cached, TimeSpan.FromHours(12));
                return cached;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                logger.LogWarning("Exchange rate provider unavailable ({Failure}); using indicative fallback rates.", ex.GetType().Name);
                cached = new(new() { ["RON"] = 1m, ["EUR"] = .201m, ["USD"] = .215m, ["GBP"] = .171m, ["CHF"] = .196m }, "fallback", null);
                cache.Set(CacheKey, cached, TimeSpan.FromMinutes(5));
                return cached;
            }
        }
        finally { Gate.Release(); }
    }
}
