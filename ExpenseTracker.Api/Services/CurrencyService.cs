using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace ExpenseTracker.Api.Services;

public class CurrencyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CurrencyService> _logger;

    public CurrencyService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<CurrencyService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<Dictionary<string, decimal>> GetRatesAsync()
    {
        if (_cache.TryGetValue("CurrencyRates", out Dictionary<string, decimal>? rates) && rates != null)
        {
            return rates;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync("https://open.er-api.com/v6/latest/RON");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var ratesDict = new Dictionary<string, decimal>();

            if (doc.RootElement.TryGetProperty("rates", out var ratesElement))
            {
                foreach (var prop in ratesElement.EnumerateObject())
                {
                    ratesDict[prop.Name] = prop.Value.GetDecimal();
                }

                _cache.Set("CurrencyRates", ratesDict, TimeSpan.FromHours(12));
                return ratesDict;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Eroare la preluarea cursurilor valutare live. Folosim fallback.");
        }

        // Fallback fix if API fails (1 RON = x Valută)
        return new Dictionary<string, decimal>
        {
            { "RON", 1m },
            { "EUR", 0.201m }, // 1 / 4.97
            { "USD", 0.215m }, // 1 / 4.65
            { "GBP", 0.171m }, // 1 / 5.82
            { "CHF", 0.196m }  // 1 / 5.10
        };
    }
}
