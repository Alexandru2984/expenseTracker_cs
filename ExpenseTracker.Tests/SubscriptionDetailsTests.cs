using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Integrations;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

[Collection("Api")]
public class SubscriptionDetailsTests(TestApiFactory factory)
{
    private static object Payload(object? details = null, string date = "2027-01-15", decimal cost = 10) =>
        new { name = "Detailed service", cost, currency = "EUR", billingPeriod = "Monthly", nextBillingDate = date, details };
    private static object Details(string notes = "Condiții,\r\nlinie cu \"ghilimele\"", string url = "HTTPS://Example.Invalid:443/account", string? trial = "2027-01-14") =>
        new { notes, providerUrl = url, trialEndsOn = trial };
    private static async Task<Guid> Create(HttpClient client, object? details = null)
    {
        var response = await client.PostAsJsonAsync("/api/subscriptions", Payload(details)); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task<JsonElement> Read(HttpClient client, Guid id) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/subscriptions/{id}");
    private static Task<HttpResponseMessage> Import(HttpClient client, string content)
    {
        var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", "details.csv");
        return client.PostAsync("/api/subscriptions/import", form);
    }

    [Fact]
    public async Task Details_persist_are_normalized_searchable_and_owner_scoped()
    {
        var (client, _) = await TestAccount.Create(factory); var (other, _) = await TestAccount.Create(factory);
        var id = await Create(client, Details());
        var detail = (await Read(client, id)).GetProperty("details");
        Assert.Equal("Condiții,\nlinie cu \"ghilimele\"", detail.GetProperty("notes").GetString());
        Assert.Equal("https://example.invalid/account", detail.GetProperty("providerUrl").GetString());
        Assert.Equal("2027-01-14", detail.GetProperty("trialEndsOn").GetString());
        var search = await client.GetFromJsonAsync<JsonElement>("/api/subscriptions?search=" + Uri.EscapeDataString("Condiții"));
        Assert.Equal(1, search.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/subscriptions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(Details("other")))).StatusCode);
        Assert.Equal(detail.GetProperty("notes").GetString(), (await Read(client, id)).GetProperty("details").GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Legacy_updates_preserve_details_and_an_explicit_details_object_can_clear_them()
    {
        var (client, _) = await TestAccount.Create(factory); var id = await Create(client, Details());
        (await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload())).EnsureSuccessStatusCode();
        Assert.Equal("2027-01-14", (await Read(client, id)).GetProperty("details").GetProperty("trialEndsOn").GetString());
        var invalid = await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(date: "2027-01-01"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        (await client.PutAsJsonAsync($"/api/subscriptions/{id}", Payload(new { }))).EnsureSuccessStatusCode();
        var details = (await Read(client, id)).GetProperty("details");
        Assert.Equal("", details.GetProperty("notes").GetString()); Assert.Equal("", details.GetProperty("providerUrl").GetString());
        Assert.Equal(JsonValueKind.Null, details.GetProperty("trialEndsOn").ValueKind);
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/subscriptions/{id}/price-history");
        Assert.Equal(0, history.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.invalid/file")]
    [InlineData("//example.invalid/account")]
    [InlineData("/relative")]
    [InlineData("https://user:password@example.invalid/account")]
    [InlineData("https://example.invalid/a b")]
    [InlineData("https://example.invalid/\npath")]
    [InlineData("https://example.invalid\\@elsewhere.invalid")]
    public async Task Unsafe_or_ambiguous_provider_links_are_rejected_without_writing(string url)
    {
        var (client, owner) = await TestAccount.Create(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/subscriptions", Payload(Details(url: url)))).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Subscriptions.AnyAsync(s => s.UserId == owner));
    }

    [Fact]
    public async Task Oversized_notes_and_URLs_that_expand_beyond_the_storage_limit_are_rejected()
    {
        var (client, _) = await TestAccount.Create(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/subscriptions", Payload(Details(notes: new string('x', 2001))))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/subscriptions", Payload(Details(url: "https://example.invalid/" + new string('ș', 1000))))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/subscriptions", Payload(Details(notes: "invalid\0control")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/subscriptions?search=" + new string('x', 201))).StatusCode);
    }

    [Theory]
    [InlineData("2027-01-16")]
    [InlineData("0001-01-01")]
    [InlineData("9999-01-01")]
    [InlineData("not-a-date")]
    public async Task Invalid_trial_dates_and_billing_before_trial_end_are_rejected(string date)
    {
        var (client, _) = await TestAccount.Create(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/subscriptions", Payload(Details(trial: date)))).StatusCode);
    }

    [Fact]
    public async Task Csv_round_trip_preserves_notes_links_and_trial_dates_and_old_files_remain_supported()
    {
        var (source, _) = await TestAccount.Create(factory); await Create(source, Details());
        var csv = await source.GetStringAsync("/api/subscriptions/export"); Assert.Contains("Notes,ProviderUrl,TrialEndsOn", csv);
        var (target, _) = await TestAccount.Create(factory);
        var result = await Import(target, csv); result.EnsureSuccessStatusCode();
        Assert.Equal(1, (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("imported").GetInt32());
        var list = await target.GetFromJsonAsync<JsonElement>("/api/subscriptions");
        var details = list.GetProperty("items")[0].GetProperty("details");
        Assert.Equal("Condiții,\nlinie cu \"ghilimele\"", details.GetProperty("notes").GetString());
        Assert.Equal("https://example.invalid/account", details.GetProperty("providerUrl").GetString());
        Assert.Equal("2027-01-14", details.GetProperty("trialEndsOn").GetString());
        var old = await Import(target, "Name,Cost,NextBillingDate\nOld,10,2027-01-15");
        Assert.Equal(1, (await old.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("imported").GetInt32());
    }

    [Theory]
    [InlineData("javascript:alert(1)", "2027-01-14")]
    [InlineData("https://example.invalid", "2027-01-16")]
    [InlineData("https://example.invalid", "not-a-date")]
    public async Task Csv_uses_the_same_provider_link_and_trial_validation(string url, string trial)
    {
        var (client, owner) = await TestAccount.Create(factory);
        var response = await Import(client, $"Name,Cost,NextBillingDate,ProviderUrl,TrialEndsOn\nInvalid,1,2027-01-15,{url},{trial}");
        response.EnsureSuccessStatusCode(); var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("rejected").GetInt32()); Assert.Equal(0, result.GetProperty("imported").GetInt32());
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Subscriptions.AnyAsync(s => s.UserId == owner));
    }

    [Fact]
    public async Task Private_details_are_excluded_from_Finance_insights_duplicates_and_calendar()
    {
        var (client, owner) = await TestAccount.Create(factory); var id = await Create(client, Details("PRIVATE_NOTE_MARKER", "https://example.invalid/PRIVATE_URL_MARKER"));
        var token = FinanceTokens.Create();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.RefreshTokens.Add(new RefreshToken { UserId = owner, TokenHash = FinanceTokens.Hash(token), ExpiresAt = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
        }
        using var finance = factory.CreateClient(); finance.DefaultRequestHeaders.Authorization = new("Bearer", token);
        foreach (var path in new[] { "/api/integrations/finance/subscriptions", $"/api/integrations/finance/subscriptions/{id}/price-history" })
        {
            var body = await finance.GetStringAsync(path); Assert.DoesNotContain("PRIVATE_", body); Assert.DoesNotContain("details", body);
        }
        foreach (var path in new[] { "/api/subscriptions/insights", "/api/subscriptions/duplicates", "/api/subscriptions/calendar" })
            Assert.DoesNotContain("PRIVATE_", await client.GetStringAsync(path));
    }

    [PostgresFact]
    public async Task Database_constraint_prevents_billing_before_trial_end_even_for_older_writers()
    {
        var (_, owner) = await TestAccount.Create(factory);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Subscriptions.Add(new SubscriptionItem { UserId = owner, Name = "Invalid trial", TrialEndsOn = new(2027, 1, 15), NextBillingDate = new(2027, 1, 14) });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<Npgsql.PostgresException>(error.InnerException);
        Assert.Equal("23514", postgres.SqlState); Assert.Equal("CK_Subscriptions_TrialBillingDate", postgres.ConstraintName);
    }
}

public class TrialPlanningTests
{
    [Fact]
    public void Trial_reminders_ignore_paused_plans_and_forecast_starts_at_the_first_saved_due_date()
    {
        var today = new DateOnly(2026, 11, 15);
        var item = new SubscriptionItem { Name = "Trial", TrialEndsOn = today.AddDays(5), NextBillingDate = today.AddDays(5), Cost = 12, Currency = "EUR" };
        var paused = new SubscriptionItem { Name = "Paused trial", TrialEndsOn = today, NextBillingDate = today, IsActive = false };
        var expired = new SubscriptionItem { Name = "Expired trial", TrialEndsOn = today.AddDays(-1), NextBillingDate = today.AddDays(29) };
        var report = SubscriptionInsights.Build([item, paused, expired], today);
        Assert.Equal(2, report.TrialCount); Assert.Equal(-1, report.Trials[0].DaysRemaining);
        Assert.Equal(5, report.Trials[1].DaysRemaining);
        Assert.DoesNotContain(report.Trials, t => t.Name == "Paused trial");
        Assert.DoesNotContain(report.Upcoming, p => p.Id == item.Id && p.Date < item.TrialEndsOn);
        Assert.Equal(12, report.Forecast[0].ByCurrency.Single(c => c.Currency == "EUR").Amount);
    }
}
