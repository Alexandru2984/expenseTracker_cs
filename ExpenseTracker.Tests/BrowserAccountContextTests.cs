using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

[Collection("Api")]
public class BrowserAccountContextTests(TestApiFactory factory)
{
    private static async Task<string> Context(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("accountContext").GetString()!;
    private static void AssertNoAuthCookies(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            Assert.DoesNotContain(values, c => c.StartsWith("access_token=") || c.StartsWith("refresh_token="));
    }

    [Fact]
    public async Task Context_is_opaque_account_specific_and_not_an_authentication_credential()
    {
        var (first, firstId) = await TestAccount.Create(factory); var (second, _) = await TestAccount.Create(factory);
        var context = await Context(first);
        Assert.Matches("^[A-F0-9]{64}$", context); Assert.Equal(context, await Context(first));
        Assert.NotEqual(context, await Context(second)); Assert.DoesNotContain(firstId.ToString("N"), context.ToLowerInvariant());
        using var anonymous = factory.CreateClient(); anonymous.DefaultRequestHeaders.Authorization = new("Bearer", context);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Changed_cookies_cannot_retarget_reads_or_writes_from_another_accounts_form()
    {
        var (first, firstId) = await TestAccount.Create(factory); var (second, secondId) = await TestAccount.Create(factory);
        second.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader, await Context(first));
        var read = await second.GetAsync("/api/subscriptions"); Assert.Equal(HttpStatusCode.Conflict, read.StatusCode);
        var write = await second.PostAsJsonAsync("/api/subscriptions", new { name = "Old account form", cost = 12, nextBillingDate = "2027-01-01" });
        Assert.Equal(HttpStatusCode.Conflict, write.StatusCode);
        Assert.Equal("account_context_changed", (await write.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        AssertNoAuthCookies(write);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Subscriptions.AnyAsync(s => s.UserId == firstId || s.UserId == secondId));
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("not-an-account-context")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Malformed_or_wrong_bindings_are_rejected_before_the_operation(string context)
    {
        var (client, _) = await TestAccount.Create(factory);
        client.DefaultRequestHeaders.TryAddWithoutValidation(AuthTokenService.AccountContextHeader, context);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/account")).StatusCode);
    }

    [Fact]
    public async Task Matching_context_works_and_legacy_clients_without_header_remain_compatible()
    {
        var (client, _) = await TestAccount.Create(factory);
        (await client.PostAsJsonAsync("/api/subscriptions", new { name = "Legacy", cost = 1, nextBillingDate = "2027-01-01" })).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader, await Context(client));
        (await client.PostAsJsonAsync("/api/subscriptions", new { name = "Bound", cost = 2, nextBillingDate = "2027-01-01" })).EnsureSuccessStatusCode();
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>("/api/subscriptions")).GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("/api/account/logout-all")]
    [InlineData("/api/account/change-password")]
    [InlineData("/api/account/confirm-email-change")]
    public async Task Old_tab_cannot_revoke_or_replace_a_new_accounts_session(string path)
    {
        var (first, _) = await TestAccount.Create(factory); var (second, _) = await TestAccount.Create(factory);
        second.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader, await Context(first));
        var response = await second.PostAsJsonAsync(path, new { currentPassword = "irrelevant-password", newPassword = "valid-new-password", code = "123456" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); AssertNoAuthCookies(response);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/auth/refresh")]
    public async Task Refresh_and_logout_validate_the_refresh_cookies_account_before_changing_cookies(string path)
    {
        var (first, _) = await TestAccount.Create(factory); var context = await Context(first);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { Username = "context_" + Guid.NewGuid().ToString("N"), EmailVerified = true, PasswordHash = BCrypt.Net.BCrypt.HashPassword("context-password") };
        db.Users.Add(user); await db.SaveChangesAsync();
        using var second = factory.CreateClient();
        var login = await second.PostAsJsonAsync("/api/auth/login", new { user.Username, password = "context-password" }); login.EnsureSuccessStatusCode();
        second.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader, context);
        var response = await second.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); AssertNoAuthCookies(response);
        using var refreshOnly = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        refreshOnly.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("refresh_token=")).Split(';')[0]);
        refreshOnly.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader, context);
        var noAccess = await refreshOnly.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.Conflict, noAccess.StatusCode); AssertNoAuthCookies(noAccess);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/auth/me")).StatusCode);
        second.DefaultRequestHeaders.Remove(AuthTokenService.AccountContextHeader);
        second.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader, await Context(second));
        (await second.PostAsync("/api/auth/refresh", null)).EnsureSuccessStatusCode();
    }
}

public class BrowserContextMacTests
{
    [Fact]
    public void Binding_uses_server_key_and_dedicated_identity_purpose()
    {
        var id = Guid.NewGuid();
        AuthTokenService Service(string key) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Secret"] = key }).Build());
        Assert.NotEqual(Service("first-test-secret-at-least-32-characters").AccountContext(id), Service("second-test-secret-at-least-32-characters").AccountContext(id));
    }
}
