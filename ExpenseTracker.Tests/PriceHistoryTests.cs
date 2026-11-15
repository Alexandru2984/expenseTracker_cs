using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Integrations;
using ExpenseTracker.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

[Collection("Api")]
public class PriceHistoryTests(TestApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static object Payload(decimal cost = 10, string currency = "EUR", string billingPeriod = "Monthly", DateTimeOffset? version = null, string name = "Price history service") =>
        new { name, cost, currency, billingPeriod, nextBillingDate = "2027-01-15", category = "SaaS", isActive = true, expectedUpdatedAt = version };

    private async Task<(HttpClient client, Guid userId, Guid id)> Subscription()
    {
        var (client, userId) = await TestAccount.Create(factory);
        var response = await client.PostAsJsonAsync("/api/subscriptions", Payload()); response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (client, userId, body.GetProperty("id").GetGuid());
    }
    private static async Task<PriceHistoryDto> Read(HttpClient client, Guid id, string query = "") =>
        (await client.GetFromJsonAsync<PriceHistoryDto>($"/api/subscriptions/{id}/price-history{query}", JsonOptions))!;

    private async Task<(HttpClient client, Guid grantId)> Finance(Guid owner, bool expired = false)
    {
        var token = FinanceTokens.Create();
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var grant = new RefreshToken { UserId = owner, TokenHash = FinanceTokens.Hash(token), ExpiresAt = DateTime.UtcNow.AddDays(expired ? -1 : 1) };
        db.RefreshTokens.Add(grant); await db.SaveChangesAsync();
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return (client, grant.Id);
    }

    [Fact]
    public async Task Creation_has_no_invented_history_and_each_price_edit_records_both_values()
    {
        var (client, _, id) = await Subscription();
        Assert.Empty((await Read(client, id)).Entries);
        var start = DateTime.UtcNow.AddSeconds(-1);
        (await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(12))).EnsureSuccessStatusCode();
        var history = await Read(client, id); var change = Assert.Single(history.Entries);
        Assert.Equal(10, change.Previous.Cost); Assert.Equal(12, change.Current.Cost);
        Assert.Equal("EUR", change.Previous.Currency); Assert.Equal(BillingPeriod.Monthly, change.Current.BillingPeriod);
        Assert.Equal(2, change.MonthlyDelta); Assert.Equal(20, change.ChangePercent);
        Assert.InRange(change.ChangedAt, start, DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(12, history.Current.Cost);
        (await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(12, name: "Renamed service"))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/subscriptions/{id}/status", new { isActive = false })).EnsureSuccessStatusCode();
        Assert.Single((await Read(client, id)).Entries);
    }

    [Fact]
    public async Task Billing_period_changes_compare_monthly_equivalents_and_currency_changes_are_not_compared()
    {
        var (client, _, id) = await Subscription();
        (await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(120, billingPeriod: "Yearly"))).EnsureSuccessStatusCode();
        var first = Assert.Single((await Read(client, id)).Entries);
        Assert.Equal(BillingPeriod.Monthly, first.Previous.BillingPeriod);
        Assert.Equal(BillingPeriod.Yearly, first.Current.BillingPeriod);
        Assert.Equal(0, first.MonthlyDelta); Assert.Equal(0, first.ChangePercent);
        (await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(120, "USD", "Yearly"))).EnsureSuccessStatusCode();
        var next = (await Read(client, id)).Entries[0];
        Assert.Equal("EUR", next.Previous.Currency); Assert.Equal("USD", next.Current.Currency);
        Assert.Null(next.MonthlyDelta); Assert.Null(next.ChangePercent);
    }

    [Fact]
    public async Task Free_price_has_no_percentage_division_and_a_decrease_has_a_negative_delta()
    {
        var (client, _, id) = await Subscription();
        (await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(0))).EnsureSuccessStatusCode();
        Assert.Equal(-100, (await Read(client, id)).Entries[0].ChangePercent);
        (await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(7))).EnsureSuccessStatusCode();
        var change = (await Read(client, id)).Entries[0]; Assert.Null(change.ChangePercent); Assert.Equal(7, change.MonthlyDelta);
    }

    [Fact]
    public async Task Stale_browser_version_returns_conflict_and_preserves_the_new_price_and_history()
    {
        var (client, _, id) = await Subscription();
        var before = await client.GetFromJsonAsync<SubscriptionResponseDto>($"/api/subscriptions/{id}", JsonOptions);
        (await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(20, version: before!.UpdatedAt))).EnsureSuccessStatusCode();
        var stale = await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(30, version: before.UpdatedAt));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var history = await Read(client, id); Assert.Equal(20, history.Current.Cost); Assert.Single(history.Entries);
    }

    [Fact]
    public async Task History_is_owned_and_Finance_grants_do_not_authorize_writes_or_other_accounts()
    {
        var (client, userId, id) = await Subscription();
        (await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(12))).EnsureSuccessStatusCode();
        var (other, otherId) = await TestAccount.Create(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/subscriptions/{id}/price-history")).StatusCode);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/subscriptions/{id}/price-history")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/integrations/finance/subscriptions/{id}/price-history")).StatusCode);
        var (reader, _) = await Finance(userId); var (foreign, _) = await Finance(otherId);
        var response = await reader.GetAsync($"/api/integrations/finance/subscriptions/{id}/price-history"); response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, body.GetProperty("history").GetProperty("total").GetInt32());
        var text = body.ToString(); Assert.DoesNotContain("userId", text); Assert.DoesNotContain("email", text); Assert.DoesNotContain("tokenHash", text);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/integrations/finance/subscriptions/{id}/price-history")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(99))).StatusCode);
        var snapshot = await reader.GetFromJsonAsync<JsonElement>("/api/integrations/finance/subscriptions");
        Assert.Equal(1, snapshot.GetProperty("schemaVersion").GetInt32());
        Assert.False(snapshot.GetProperty("subscriptions")[0].TryGetProperty("priceHistory", out _));
    }

    [Fact]
    public async Task Expired_and_revoked_Finance_grants_cannot_read_history()
    {
        var (_, userId, id) = await Subscription();
        var (expired, _) = await Finance(userId, expired: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await expired.GetAsync($"/api/integrations/finance/subscriptions/{id}/price-history")).StatusCode);
        var (reader, grantId) = await Finance(userId);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.RefreshTokens.FindAsync(grantId))!.RevokedAt = DateTime.UtcNow; await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync($"/api/integrations/finance/subscriptions/{id}/price-history")).StatusCode);
    }

    [Fact]
    public async Task History_paginates_with_stable_order_and_bounded_page_sizes()
    {
        var (client, _, id) = await Subscription();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var timestamp = DateTime.UtcNow;
            db.SubscriptionPriceChanges.AddRange(Enumerable.Range(0, 105).Select(_ => new SubscriptionPriceChange { SubscriptionId = id,
                ChangedAt = timestamp, PreviousCost = 10, Cost = 11, Currency = "EUR", PreviousCurrency = "EUR" }));
            await db.SaveChangesAsync();
        }
        var first = await Read(client, id, "?take=999&skip=-1");
        Assert.Equal(105, first.Total); Assert.Equal(100, first.Entries.Count); Assert.Equal(100, first.Take); Assert.Equal(0, first.Skip);
        var next = await Read(client, id, "?take=100&skip=100"); Assert.Equal(5, next.Entries.Count);
        Assert.Empty(first.Entries.Select(e => e.Id).Intersect(next.Entries.Select(e => e.Id)));
        Assert.Equal(first.Entries.Select(e => e.Id), (await Read(client, id, "?take=100")).Entries.Select(e => e.Id));
        Assert.Single((await Read(client, id, "?take=0")).Entries);
    }

    [PostgresFact]
    public async Task Concurrent_price_edits_roll_back_the_losing_event_and_retry_does_not_duplicate_it()
    {
        var (client, _, id) = await Subscription();
        using var a = factory.Services.CreateScope(); using var b = factory.Services.CreateScope();
        var first = a.ServiceProvider.GetRequiredService<AppDbContext>(); var second = b.ServiceProvider.GetRequiredService<AppDbContext>();
        var one = (await first.Subscriptions.FindAsync(id))!; var two = (await second.Subscriptions.FindAsync(id))!;
        one.Cost = 20; two.Cost = 30;
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        Assert.Single((await Read(client, id)).Entries);
        await second.Entry(two).ReloadAsync(); two.Cost = 30; await second.SaveChangesAsync();
        var history = await Read(client, id); Assert.Equal(2, history.Entries.Count);
        Assert.Equal(20, history.Entries[0].Previous.Cost); Assert.Equal(30, history.Entries[0].Current.Cost);
    }

    [PostgresFact]
    public async Task Deleting_a_subscription_also_deletes_its_price_history()
    {
        var (client, _, id) = await Subscription();
        (await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(12))).EnsureSuccessStatusCode();
        Assert.Single((await Read(client, id)).Entries);
        (await client.DeleteAsync($"/api/subscriptions/{id}")).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.SubscriptionPriceChanges.Where(c => c.SubscriptionId == id).ToListAsync());
    }
}
