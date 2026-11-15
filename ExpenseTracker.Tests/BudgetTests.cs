using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

[Collection("Api")]
public class BudgetTests(TestApiFactory factory)
{
    private static async Task<JsonElement> Create(HttpClient client, string currency = "EUR", string category = "", decimal limit = 100m)
    {
        var response = await client.PostAsJsonAsync("/api/budgets", new { currency, category, monthlyLimit = limit });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Planned_amounts_use_all_owned_active_items_and_keep_currencies_and_categories_separate()
    {
        var (client, owner) = await TestAccount.Create(factory); var (_, other) = await TestAccount.Create(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Subscriptions.AddRange(
                new SubscriptionItem { UserId=owner, Name="Monthly", Cost=20, Currency="EUR", Category="SaaS" },
                new SubscriptionItem { UserId=owner, Name="Annual", Cost=120, Currency="EUR", Category="SaaS", BillingPeriod=BillingPeriod.Yearly },
                new SubscriptionItem { UserId=owner, Name="Other currency", Cost=50, Currency="USD", Category="SaaS" },
                new SubscriptionItem { UserId=owner, Name="Uncategorised", Cost=5, Currency="EUR" },
                new SubscriptionItem { UserId=owner, Name="Paused", Cost=999, Currency="EUR", Category="SaaS", IsActive=false },
                new SubscriptionItem { UserId=other, Name="Other private account", Cost=9999, Currency="EUR", Category="SaaS" });
            await db.SaveChangesAsync();
        }
        await Create(client, category:"saas", limit:25m); await Create(client); await Create(client, currency:"USD"); await Create(client, category:"Altele");
        var response = await client.GetAsync("/api/budgets"); response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var budgets = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        var saas = budgets.Single(b => b.GetProperty("category").GetString()=="saas");
        Assert.Equal(30m, saas.GetProperty("monthlyPlanned").GetDecimal()); Assert.Equal(-5m, saas.GetProperty("remaining").GetDecimal());
        Assert.True(saas.GetProperty("isOverBudget").GetBoolean()); Assert.Equal(120m, saas.GetProperty("usagePercent").GetDecimal());
        Assert.Equal(35m, budgets.Single(b => b.GetProperty("currency").GetString()=="EUR" && b.GetProperty("category").GetString()=="").GetProperty("monthlyPlanned").GetDecimal());
        Assert.Equal(50m, budgets.Single(b => b.GetProperty("currency").GetString()=="USD").GetProperty("monthlyPlanned").GetDecimal());
        Assert.Equal(5m, budgets.Single(b => b.GetProperty("category").GetString()=="Altele").GetProperty("monthlyPlanned").GetDecimal());
        Assert.DoesNotContain("userId", string.Join("", budgets));
    }

    [Fact]
    public async Task Budgets_are_owner_scoped_on_list_update_and_delete()
    {
        var (owner, _) = await TestAccount.Create(factory); var (other, _) = await TestAccount.Create(factory);
        var saved = await Create(owner); var id = saved.GetProperty("id").GetGuid(); var version = saved.GetProperty("version").GetGuid();
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/budgets")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/budgets/{id}", new { currency="EUR", monthlyLimit=25, version })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/budgets/{id}?version={version}")).StatusCode);
        Assert.Single((await owner.GetFromJsonAsync<JsonElement>("/api/budgets")).EnumerateArray());
    }

    [Fact]
    public async Task Duplicate_categories_are_normalized_and_stale_updates_and_deletes_fail_without_overwriting()
    {
        var (client, _) = await TestAccount.Create(factory);
        var saved = await Create(client, category:"SaaS"); var id = saved.GetProperty("id").GetGuid(); var version = saved.GetProperty("version").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/budgets", new { currency="eur", category=" saas ", monthlyLimit=80 })).StatusCode);
        var update = await client.PutAsJsonAsync($"/api/budgets/{id}", new { currency="EUR", category="SaaS", monthlyLimit=80, version });
        update.EnsureSuccessStatusCode(); var newVersion = (await update.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetGuid();
        Assert.NotEqual(version,newVersion);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/budgets/{id}", new { currency="EUR", category="SaaS", monthlyLimit=1, version })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/budgets/{id}?version={version}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/budgets/{id}")).StatusCode);
        var current = (await client.GetFromJsonAsync<JsonElement>("/api/budgets"))[0]; Assert.Equal(80m,current.GetProperty("monthlyLimit").GetDecimal());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/budgets/{id}?version={newVersion}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/budgets")).EnumerateArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000001)]
    [InlineData(1.234)]
    public async Task Invalid_limits_are_rejected(decimal monthlyLimit)
    {
        var (client, _) = await TestAccount.Create(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/budgets", new { currency="EUR", monthlyLimit })).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/budgets")).EnumerateArray());
    }

    [PostgresFact]
    public async Task Concurrent_updates_with_the_same_version_have_one_winner()
    {
        var (client, _) = await TestAccount.Create(factory); var saved = await Create(client);
        var id = saved.GetProperty("id").GetGuid(); var version = saved.GetProperty("version").GetGuid();
        var results = await Task.WhenAll(client.PutAsJsonAsync($"/api/budgets/{id}", new { currency="EUR", monthlyLimit=50, version }),
            client.PutAsJsonAsync($"/api/budgets/{id}", new { currency="EUR", monthlyLimit=60, version }));
        Assert.Equal(1,results.Count(r=>r.StatusCode==HttpStatusCode.OK)); Assert.Equal(1,results.Count(r=>r.StatusCode==HttpStatusCode.Conflict));
    }
}
