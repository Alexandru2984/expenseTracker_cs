using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

[Collection("Api")]
public class SessionSecurityTests(TestApiFactory factory)
{
    private const string Password = "session-security-password";

    private async Task<(HttpClient client, HttpClient captured, User user)> Login()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Username = "session_" + Guid.NewGuid().ToString("N"),
            Email = Guid.NewGuid().ToString("N") + "@example.invalid", EmailVerified = true,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password)
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { user.Username, password = Password });
        login.EnsureSuccessStatusCode();
        var cookies = login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).ToArray();
        var csrf = Uri.UnescapeDataString(cookies.Single(c => c.StartsWith("csrf_token="))["csrf_token=".Length..]);
        client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        var captured = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        captured.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        captured.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        return (client, captured, user);
    }

    [Theory]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/account/logout-all")]
    public async Task Logout_revokes_captured_access_and_refresh_tokens_immediately(string endpoint)
    {
        var (client, captured, _) = await Login();
        Assert.Equal(HttpStatusCode.OK, (await captured.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(endpoint, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await captured.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await captured.PostAsync("/api/auth/refresh", null)).StatusCode);
    }

    [Fact]
    public async Task Password_change_revokes_old_access_codes_and_finance_grants_but_keeps_new_session()
    {
        var (client, captured, user) = await Login();
        var grantResponse = await client.PostAsJsonAsync("/api/account/finance-tokens", new { currentPassword = Password });
        grantResponse.EnsureSuccessStatusCode();
        var grant = await grantResponse.Content.ReadFromJsonAsync<JsonElement>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.VerificationCodes.Add(new VerificationCode { UserId = user.Id, Purpose = VerificationPurpose.PasswordReset,
                CodeHash = scope.ServiceProvider.GetRequiredService<AuthTokenService>().HashVerificationCode(user, VerificationPurpose.PasswordReset, "123456"), ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
            await db.SaveChangesAsync();
        }
        var response = await client.PostAsJsonAsync("/api/account/change-password",
            new { currentPassword = Password, newPassword = "new-session-password" });
        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await captured.GetAsync("/api/subscriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var financeReader = factory.CreateClient();
        financeReader.DefaultRequestHeaders.Authorization = new("Bearer", grant.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await financeReader.GetAsync("/api/integrations/finance/subscriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password",
            new { user.Email, code = "123456", newPassword = "attacker-reset-password" })).StatusCode);
    }

    [Fact]
    public async Task Email_change_requires_password_and_invalidates_previous_inbox_codes()
    {
        var (client, captured, user) = await Login();
        var email = Guid.NewGuid().ToString("N") + "@example.invalid";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/account/change-email", new { email })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/account/change-email", new { email, currentPassword = "wrong" })).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.VerificationCodes.Add(new VerificationCode { UserId = user.Id, CodeHash = scope.ServiceProvider.GetRequiredService<AuthTokenService>().HashVerificationCode(user, VerificationPurpose.EmailVerification, "123456"), ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
            await db.SaveChangesAsync();
        }
        (await client.PostAsJsonAsync("/api/account/change-email", new { email, currentPassword = Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await captured.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/account")).StatusCode);
        using var check = factory.Services.CreateScope();
        var oldCode = await check.ServiceProvider.GetRequiredService<AppDbContext>().VerificationCodes
            .SingleAsync(c => c.UserId == user.Id && c.ConsumedAt != null);
        Assert.NotNull(oldCode.ConsumedAt);
    }

    [Fact]
    public async Task Refresh_rotates_session_and_replay_revokes_replacement()
    {
        var (client, captured, _) = await Login();
        (await client.PostAsync("/api/auth/refresh", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await captured.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await captured.PostAsync("/api/auth/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("https://attacker.invalid")]
    [InlineData("https://evil.expenses.micutu.com")]
    [InlineData("null")]
    public async Task Auth_mutations_reject_untrusted_browser_origins(string origin)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_rotation_of_the_same_row_is_rejected_by_the_database()
    {
        var (_, _, user) = await Login();
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var a = await first.RefreshTokens.SingleAsync(t => t.UserId == user.Id);
        var b = await second.RefreshTokens.SingleAsync(t => t.UserId == user.Id);
        a.RevokedAt = DateTime.UtcNow;
        await first.SaveChangesAsync();
        b.RevokedAt = DateTime.UtcNow;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Oversized_passwords_do_not_throw_or_authenticate_by_truncation()
    {
        var (_, _, user) = await Login();
        using var anonymous = factory.CreateClient();
        foreach (var password in new[] { new string('a', 100), new string('é', 50) })
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonymous.PostAsJsonAsync("/api/auth/login", new { user.Username, password })).StatusCode);
    }

    [Fact]
    public async Task Stale_email_verification_cannot_overwrite_a_concurrent_inbox_change()
    {
        var (_, _, user) = await Login();
        using (var seedScope = factory.Services.CreateScope())
        {
            var seed = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await seed.Users.FindAsync(user.Id))!.EmailVerified = false;
            await seed.SaveChangesAsync();
        }
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var verifying = (await first.Users.FindAsync(user.Id))!;
        var changing = (await second.Users.FindAsync(user.Id))!;
        changing.Email = Guid.NewGuid().ToString("N") + "@example.invalid";
        await second.SaveChangesAsync();
        verifying.EmailVerified = true;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => first.SaveChangesAsync());
    }
}
