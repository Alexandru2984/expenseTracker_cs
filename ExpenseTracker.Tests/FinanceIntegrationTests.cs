using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Integrations;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

[Collection("Api")]
public class FinanceIntegrationTests(TestApiFactory factory)
{
    private const string Password = "integration-test-password";

    private async Task<(HttpClient client, Guid userId)> Account()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { Username = "finance_" + Guid.NewGuid().ToString("N"), PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password), EmailVerified = true };
        db.Users.Add(user);
        var session = new RefreshToken { UserId = user.Id, TokenHash = AuthTokenService.Hash(Guid.NewGuid().ToString()), ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.RefreshTokens.Add(session);
        await db.SaveChangesAsync();
        var jwt = scope.ServiceProvider.GetRequiredService<AuthTokenService>().CreateAccessToken(user, session.Id).token;
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"access_token={jwt}; csrf_token=integration-test-csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", "integration-test-csrf");
        return (client, user.Id);
    }

    private async Task<(string token, Guid id)> Grant(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/account/finance-tokens", new { currentPassword = Password, validDays = 30 });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("token").GetString()!, json.GetProperty("id").GetGuid());
    }

    private HttpClient WithToken(string token)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public void Dedicated_tokens_have_entropy_format_and_a_separate_hash_namespace()
    {
        var first = FinanceTokens.Create();
        Assert.True(FinanceTokens.IsValidFormat(first));
        Assert.NotEqual(first, FinanceTokens.Create());
        Assert.StartsWith("finance:", FinanceTokens.Hash(first));
        Assert.NotEqual(AuthTokenService.Hash(first), FinanceTokens.Hash(first));
        Assert.False(FinanceTokens.IsValidFormat(null));
        Assert.False(FinanceTokens.IsValidFormat("fin_" + new string('!',43)));
    }

    [Fact]
    public async Task Token_creation_requires_password_and_never_returns_raw_tokens_in_listing()
    {
        var (client, owner) = await Account();
        var wrong = await client.PostAsJsonAsync("/api/account/finance-tokens", new { currentPassword = "wrong", validDays = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var (token, _) = await Grant(client);
        var list = await client.GetStringAsync("/api/account/finance-tokens");
        Assert.DoesNotContain(token, list);
        Assert.DoesNotContain("tokenHash", list);
        using var scope = factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RefreshTokens.SingleAsync(t=>t.UserId==owner && t.TokenHash.StartsWith(FinanceTokens.HashNamespace));
        Assert.Equal(FinanceTokens.Hash(token),row.TokenHash);
    }

    [Fact]
    public async Task Export_returns_only_grant_owners_subscriptions_and_cannot_authorize_normal_routes()
    {
        var (owner, ownerId) = await Account();
        var (_, otherId) = await Account();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Subscriptions.Add(new SubscriptionItem { UserId=ownerId, Name="Owner subscription", Cost=12.5m, Currency="EUR", NextBillingDate=new DateOnly(2026,12,1) });
            db.Subscriptions.Add(new SubscriptionItem { UserId=otherId, Name="Private other account", Cost=999m, Currency="RON", NextBillingDate=new DateOnly(2026,12,1) });
            await db.SaveChangesAsync();
        }
        var (token,_) = await Grant(owner);
        using var reader = WithToken(token);
        var response = await reader.GetAsync("/api/integrations/finance/subscriptions");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1,json.GetProperty("schemaVersion").GetInt32());
        Assert.Single(json.GetProperty("subscriptions").EnumerateArray());
        var body = json.ToString();
        Assert.Contains("Owner subscription",body);
        Assert.DoesNotContain("Private other account",body);
        Assert.DoesNotContain("userId",body);
        Assert.Equal("no-store",response.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized,(await reader.GetAsync("/api/subscriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await reader.GetAsync("/api/account/finance-tokens")).StatusCode);
        // Cookie authentication alone never authorizes the scoped export.
        Assert.Equal(HttpStatusCode.Unauthorized,(await owner.GetAsync("/api/integrations/finance/subscriptions")).StatusCode);
    }

    [Fact]
    public async Task Revocation_is_owner_scoped_and_invalidates_the_export_immediately()
    {
        var (owner,_) = await Account(); var (other,_) = await Account();
        var (token,id) = await Grant(owner);
        Assert.Equal(HttpStatusCode.NotFound,(await other.DeleteAsync($"/api/account/finance-tokens/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await owner.DeleteAsync($"/api/account/finance-tokens/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await WithToken(token).GetAsync("/api/integrations/finance/subscriptions")).StatusCode);
    }

    [Fact]
    public async Task Expired_tokens_and_global_logout_disable_the_integration()
    {
        var (owner,_) = await Account(); var (token,id) = await Grant(owner);
        using (var scope = factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.RefreshTokens.FindAsync(id))!.ExpiresAt=DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized,(await WithToken(token).GetAsync("/api/integrations/finance/subscriptions")).StatusCode);
        var (active,_) = await Grant(owner);
        Assert.Equal(HttpStatusCode.NoContent,(await owner.PostAsync("/api/account/logout-all",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await WithToken(active).GetAsync("/api/integrations/finance/subscriptions")).StatusCode);
    }

    [Fact]
    public async Task Integration_token_cannot_be_exchanged_for_a_normal_session()
    {
        var (owner,_) = await Account(); var (token,_) = await Grant(owner);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie",$"refresh_token={token}");
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsync("/api/auth/refresh",null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await WithToken(token).GetAsync("/api/integrations/finance/subscriptions")).StatusCode);
    }
}
