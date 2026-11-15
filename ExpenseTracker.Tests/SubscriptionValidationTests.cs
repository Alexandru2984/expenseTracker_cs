using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Infrastructure;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

internal static class TestAccount
{
    public static async Task<(HttpClient client, Guid userId)> Create(TestApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { Username = "data_" + Guid.NewGuid().ToString("N"), EmailVerified = true, PasswordHash = "synthetic-hash" };
        var session = new RefreshToken { UserId = user.Id, TokenHash = AuthTokenService.Hash(Guid.NewGuid().ToString()), ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.Users.Add(user); db.RefreshTokens.Add(session); await db.SaveChangesAsync();
        var jwt = scope.ServiceProvider.GetRequiredService<AuthTokenService>().CreateAccessToken(user, session.Id).token;
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"access_token={jwt}; csrf_token=synthetic-csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", "synthetic-csrf");
        return (client, user.Id);
    }
}

[Collection("Api")]
public class SubscriptionValidationTests(TestApiFactory factory)
{
    [Theory]
    [InlineData("\"billingPeriod\":\"Unknown\"")]
    [InlineData("\"billingPeriod\":99")]
    [InlineData("\"currency\":\"<script>\"")]
    [InlineData("\"nextBillingDate\":\"0001-01-01\"")]
    [InlineData("\"cost\":-1")]
    [InlineData("\"cost\":1.234")]
    public async Task Invalid_financial_values_are_rejected_without_writing(string invalidProperty)
    {
        var (client, userId) = await TestAccount.Create(factory);
        var properties = new Dictionary<string, object> { ["name"] = "Valid", ["cost"] = 5m,
            ["currency"] = "EUR", ["billingPeriod"] = "Monthly", ["nextBillingDate"] = "2026-12-01" };
        using var invalid = JsonDocument.Parse("{" + invalidProperty + "}");
        foreach (var property in invalid.RootElement.EnumerateObject()) properties[property.Name] = property.Value.Clone();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/subscriptions", properties)).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Subscriptions.AnyAsync(s => s.UserId == userId));
    }

    [Fact]
    public async Task Missing_billing_date_is_rejected_and_creation_honors_inactive_status()
    {
        var (client, _) = await TestAccount.Create(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/subscriptions", new { name = "Missing date", cost = 4 })).StatusCode);
        var response = await client.PostAsJsonAsync("/api/subscriptions", new { name = "  Paused  ", cost = 4.5,
            currency = "eur", nextBillingDate = "2026-12-01", isActive = false });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Paused", json.GetProperty("name").GetString());
        Assert.Equal("EUR", json.GetProperty("currency").GetString());
        Assert.False(json.GetProperty("isActive").GetBoolean());
    }

    private static async Task<HttpResponseMessage> Import(HttpClient client, string csv)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "subscriptions.csv");
        return await client.PostAsync("/api/subscriptions/import", form);
    }

    [Theory]
    [InlineData("negative,-2,2026-12-01,Monthly")]
    [InlineData("overflow,1000001,2026-12-01,Monthly")]
    [InlineData("rounding,1.234,2026-12-01,Monthly")]
    [InlineData("undefined,1,2026-12-01,99")]
    [InlineData("bad-date,1,12/01/2026,Monthly")]
    public async Task Csv_rejects_bad_rows_without_silently_changing_their_values(string row)
    {
        var (client, _) = await TestAccount.Create(factory);
        var response = await Import(client, "Name,Cost,NextBillingDate,BillingPeriod\r\n" + row);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("imported").GetInt32());
        Assert.Equal(1, json.GetProperty("rejected").GetInt32());
        Assert.Single(json.GetProperty("errors").EnumerateArray());
    }

    [Theory]
    [InlineData("Name,Cost,NextBillingDate\n\"unclosed,1,2026-12-01")]
    [InlineData("Name,Cost,NextBillingDate,Name\nA,1,2026-12-01,B")]
    public async Task Malformed_csv_returns_400(string csv)
    {
        var (client, _) = await TestAccount.Create(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await Import(client, csv)).StatusCode);
    }

    [Fact]
    public async Task Csv_supports_bom_and_quotes_reports_duplicates_and_preserves_ownership()
    {
        var (client, userId) = await TestAccount.Create(factory);
        var (_, otherId) = await TestAccount.Create(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Subscriptions.Add(new SubscriptionItem { UserId = otherId, Name = "Shared name", NextBillingDate = new(2026,12,1) });
            await db.SaveChangesAsync();
        }
        var response = await Import(client, "\uFEFFName,Cost,NextBillingDate,Currency\r\n\"Shared name\",12.50,2026-12-01,eur\r\nShared name,12.50,2026-12-01,EUR");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("imported").GetInt32());
        Assert.Equal(1, json.GetProperty("skipped").GetInt32());
        using var check = factory.Services.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<AppDbContext>().Subscriptions.SingleAsync(s => s.UserId == userId);
        Assert.Equal(12.50m, saved.Cost); Assert.Equal("EUR", saved.Currency);
    }

    [Fact]
    public void Csv_parser_rejects_excess_rows_columns_and_malformed_quotes()
    {
        Assert.Throws<FormatException>(() => Csv.Parse("a,b,c", maxColumns: 2));
        Assert.Throws<FormatException>(() => Csv.Parse("a\nb\nc", maxRows: 2));
        Assert.Throws<FormatException>(() => Csv.Parse("\"closed\"garbage,b"));
        Assert.StartsWith("\"'", Csv.Field("   =SUM(A1)"));
        Assert.StartsWith("\"'", Csv.Field("\n=SUM(A1)"));
    }
}
