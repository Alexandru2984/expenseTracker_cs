using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

public class DuplicateMatchingTests
{
    private static DuplicateCandidate Candidate(string name, decimal cost = 10, string currency = "EUR", bool active = true, BillingPeriod period = BillingPeriod.Monthly) =>
        new(Guid.NewGuid(), name, cost, currency, period, new DateOnly(2027, 1, 15), "SaaS", active);

    [Fact]
    public void Matching_normalizes_spacing_case_and_unicode_without_mixing_currencies()
    {
        var items = new[] { Candidate("  Café   Plus "), Candidate("cafe\u0301 plus", 120, period: BillingPeriod.Yearly),
            Candidate("CAFÉ PLUS", currency: "USD"), Candidate("Other"), Candidate("Café Plus", active: false) };
        var report = DuplicateSubscriptions.Build(items, false); var group = Assert.Single(report.Groups);
        Assert.Equal(4, report.CheckedCount); Assert.Equal("EUR", group.Currency); Assert.Equal(2, group.Items.Count);
        Assert.Equal(20, group.ActiveMonthlyTotal);
        var inclusive = Assert.Single(DuplicateSubscriptions.Build(items, true).Groups);
        Assert.Equal(3, inclusive.Items.Count); Assert.Equal(20, inclusive.ActiveMonthlyTotal);
    }

    [Fact]
    public void Empty_names_do_not_become_a_duplicate_group_and_matching_does_not_mutate_records()
    {
        var first = Candidate(" \t "); var second = Candidate(""); var valid = Candidate("Original Name");
        Assert.Empty(DuplicateSubscriptions.Build([first, second, valid], true).Groups);
        Assert.Equal("Original Name", valid.Name); Assert.True(valid.IsActive);
    }
}

[Collection("Api")]
public class DuplicateApiTests(TestApiFactory factory)
{
    [Fact]
    public async Task Report_covers_all_pages_and_other_accounts_are_excluded_without_changing_anything()
    {
        var (client, owner) = await TestAccount.Create(factory); var (_, other) = await TestAccount.Create(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 12; i++) db.Subscriptions.Add(new SubscriptionItem { UserId = owner, Name = "Unique " + i, Currency = "EUR" });
            db.Subscriptions.Add(new SubscriptionItem { UserId = owner, Name = "Same Name", Cost = 10, Currency = "EUR" });
            db.Subscriptions.Add(new SubscriptionItem { UserId = owner, Name = " SAME  NAME ", Cost = 120, Currency = "EUR", BillingPeriod = BillingPeriod.Yearly });
            db.Subscriptions.Add(new SubscriptionItem { UserId = owner, Name = "Same Name", Cost = 999, Currency = "EUR", IsActive = false });
            db.Subscriptions.Add(new SubscriptionItem { UserId = other, Name = "PRIVATE OTHER", Currency = "EUR" });
            db.Subscriptions.Add(new SubscriptionItem { UserId = other, Name = "private other", Currency = "EUR" });
            await db.SaveChangesAsync();
        }
        var response = await client.GetAsync("/api/subscriptions/duplicates?take=1&search=unrelated"); response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl!.NoStore);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(14, report.GetProperty("checkedCount").GetInt32());
        var group = Assert.Single(report.GetProperty("groups").EnumerateArray());
        Assert.Equal(2, group.GetProperty("items").GetArrayLength()); Assert.Equal(20, group.GetProperty("activeMonthlyTotal").GetDecimal());
        Assert.DoesNotContain("PRIVATE", report.ToString()); Assert.DoesNotContain("userId", report.ToString());
        var inclusive = await client.GetFromJsonAsync<JsonElement>("/api/subscriptions/duplicates?includeInactive=true");
        Assert.Equal(3, inclusive.GetProperty("groups")[0].GetProperty("items").GetArrayLength());
        using var check = factory.Services.CreateScope(); var dbCheck = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(15, await dbCheck.Subscriptions.CountAsync(s => s.UserId == owner));
        Assert.Empty(await dbCheck.SubscriptionPriceChanges.Where(c => c.Subscription.UserId == owner).ToListAsync());
    }

    [Fact]
    public async Task Anonymous_access_is_rejected_and_an_empty_account_gets_an_empty_report()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/subscriptions/duplicates")).StatusCode);
        var (client, _) = await TestAccount.Create(factory);
        var report = await client.GetFromJsonAsync<JsonElement>("/api/subscriptions/duplicates");
        Assert.Equal(0, report.GetProperty("checkedCount").GetInt32()); Assert.Empty(report.GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public async Task Oversized_reports_are_rejected_instead_of_silently_omitting_matches()
    {
        var (client, owner) = await TestAccount.Create(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Subscriptions.AddRange(Enumerable.Range(0, 5001).Select(i => new SubscriptionItem { UserId = owner, Name = "Bulk " + i }));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.GetAsync("/api/subscriptions/duplicates")).StatusCode);
    }
}
