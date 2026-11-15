using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Infrastructure;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

public class ForecastTests
{
    [Fact]
    public void Monthly_forecast_preserves_anchor_day_across_short_months()
    {
        var item = new SubscriptionItem { NextBillingDate = new(2027, 1, 31) };
        Assert.Equal(new[] { new DateOnly(2027, 1, 31), new(2027, 2, 28), new(2027, 3, 31) },
            SubscriptionInsights.Occurrences(item, new(2027, 1, 1), new(2027, 4, 1)));
    }

    [Fact]
    public void Annual_forecast_handles_leap_days_and_skips_past_occurrences()
    {
        var item = new SubscriptionItem { BillingPeriod = BillingPeriod.Yearly, NextBillingDate = new(2024, 2, 29) };
        Assert.Equal(new[] { new DateOnly(2027, 2, 28), new(2028, 2, 29) },
            SubscriptionInsights.Occurrences(item, new(2026, 3, 1), new(2029, 1, 1)));
    }

    [Fact]
    public void Forecast_keeps_currencies_separate_books_annual_payments_in_full_and_excludes_paused_items()
    {
        var today = new DateOnly(2027, 1, 15);
        var items = new[]
        {
            new SubscriptionItem { Name="Monthly", Cost=10m, Currency="EUR", Category="SaaS", NextBillingDate=new(2027,1,20) },
            new SubscriptionItem { Name="Yearly", Cost=120m, Currency="USD", Category="SaaS", BillingPeriod=BillingPeriod.Yearly, NextBillingDate=new(2027,2,1) },
            new SubscriptionItem { Name="Paused", Cost=999m, Currency="RON", IsActive=false, NextBillingDate=new(2027,1,16) },
            new SubscriptionItem { Name="Past due", Cost=3m, Currency="EUR", NextBillingDate=new(2026,12,1) }
        };
        var analysis = SubscriptionInsights.Build(items, today);
        Assert.Equal(3, analysis.ActiveCount); Assert.Equal(1, analysis.InactiveCount);
        Assert.Equal(1, analysis.OverdueCount);
        Assert.Equal(10m, analysis.Forecast[0].ByCurrency.Single().Amount);
        Assert.Equal(120m, analysis.Forecast[1].ByCurrency.Single(c => c.Currency == "USD").Amount);
        Assert.DoesNotContain(analysis.Forecast.SelectMany(m => m.ByCurrency), c => c.Currency == "RON");
        Assert.Equal(999m, analysis.PausedMonthly.Single().Amount);
        Assert.Equal(10m, analysis.ByCategory.Single(c => c.Currency == "USD").MonthlyTotal);
        Assert.Equal(new DateOnly(2026,12,1), items[3].NextBillingDate); // forecast never mutates records
    }

    [Fact]
    public void Calendar_escapes_injection_and_folds_by_utf8_octets()
    {
        var name = string.Concat(Enumerable.Repeat("Abonament românesc șț;🧾", 8)) + "\r\nBEGIN:VALARM";
        var bytes = CalendarExport.Build(new[] { new SubscriptionItem { Name=name, Cost=12.50m, NextBillingDate=new(2027,1,31) } }, DateTime.UtcNow);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", text);
        Assert.Contains("DTSTART;VALUE=DATE:20270131", text);
        Assert.DoesNotContain("\r\nBEGIN:VALARM", text);
        foreach (var line in text.Split("\r\n")) Assert.True(Encoding.UTF8.GetByteCount(line) <= 75);
        var unfolded = text.Replace("\r\n ", "");
        Assert.Contains("\\nBEGIN:VALARM", unfolded); Assert.Contains("șț\\;🧾", unfolded);
    }
}

[Collection("Api")]
public class PlanningIntegrationTests(TestApiFactory factory)
{
    [Fact]
    public async Task Insights_and_calendar_cover_all_pages_and_preserve_account_isolation()
    {
        var (client, ownerId) = await TestAccount.Create(factory);
        var (_, otherId) = await TestAccount.Create(factory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i=0; i<15; i++) db.Subscriptions.Add(new SubscriptionItem { UserId=ownerId, Name="Own "+i, Cost=10m, Currency="EUR", NextBillingDate=today.AddDays(1), Category="Own category" });
            db.Subscriptions.Add(new SubscriptionItem { UserId=otherId, Name="Private-other-account", Cost=9999m, Currency="EUR", NextBillingDate=today });
            await db.SaveChangesAsync();
        }
        var response = await client.GetAsync("/api/subscriptions/insights"); response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(15, json.GetProperty("activeCount").GetInt32());
        Assert.Equal(150m, json.GetProperty("byCategory")[0].GetProperty("monthlyTotal").GetDecimal());
        Assert.DoesNotContain("Private-other-account", json.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var calendar = await client.GetStringAsync("/api/subscriptions/calendar");
        Assert.Equal(15, calendar.Split("BEGIN:VEVENT").Length-1);
        Assert.DoesNotContain("Private-other-account", calendar);
    }

    [Fact]
    public async Task Pause_and_resume_are_owner_scoped_idempotent_and_reflected_in_summary()
    {
        var (client, ownerId) = await TestAccount.Create(factory);
        var (other, _) = await TestAccount.Create(factory);
        var created = await client.PostAsJsonAsync("/api/subscriptions", new { name="Switchable", cost=12, nextBillingDate="2027-01-01" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/subscriptions/{id}/status", new { isActive=false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/subscriptions/{id}/status", new { })).StatusCode);
        for (var i=0; i<2; i++) (await client.PostAsJsonAsync($"/api/subscriptions/{id}/status", new { isActive=false })).EnsureSuccessStatusCode();
        var summary = await client.GetFromJsonAsync<JsonElement>("/api/subscriptions/summary");
        Assert.Equal(0, summary.GetProperty("activeSubscriptions").GetInt32());
        (await client.PostAsJsonAsync($"/api/subscriptions/{id}/status", new { isActive=true })).EnsureSuccessStatusCode();
        summary = await client.GetFromJsonAsync<JsonElement>("/api/subscriptions/summary");
        Assert.Equal(1, summary.GetProperty("activeSubscriptions").GetInt32());
    }

    [Fact]
    public async Task Session_list_never_exposes_credentials_and_revocation_is_owner_scoped()
    {
        var (client, _) = await TestAccount.Create(factory);
        var (other, _) = await TestAccount.Create(factory);
        var response = await client.GetStringAsync("/api/account/sessions");
        Assert.DoesNotContain("tokenHash", response); Assert.DoesNotContain("credential", response);
        var sessions = JsonSerializer.Deserialize<JsonElement>(response);
        Assert.Single(sessions.EnumerateArray()); Assert.True(sessions[0].GetProperty("isCurrent").GetBoolean());
        var id = sessions[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/account/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/account/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}
