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

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EXPENSE_TEST_POSTGRES")))
            Skip = "Requires the disposable PostgreSQL test database.";
    }
}

[Collection("Api")]
public class PendingEmailTests(TestApiFactory factory)
{
    private const string Password = "pending-email-test-password";

    private async Task<(HttpClient client, HttpClient captured, User user)> Account()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { Username = "pending_" + Guid.NewGuid().ToString("N"),
            Email = Guid.NewGuid().ToString("N") + "@example.invalid", EmailVerified = true,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password) };
        db.Users.Add(user); await db.SaveChangesAsync();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { user.Username, password = Password });
        response.EnsureSuccessStatusCode();
        var cookies = response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).ToArray();
        var csrf = Uri.UnescapeDataString(cookies.Single(c => c.StartsWith("csrf_token="))["csrf_token=".Length..]);
        client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        var captured = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        captured.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        captured.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        return (client, captured, user);
    }

    private static string NewEmail() => Guid.NewGuid().ToString("N") + "@example.invalid";
    private static Task<HttpResponseMessage> Request(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/account/change-email", new { email, currentPassword = Password });

    private async Task KnownCode(Guid id, string code = "123456", bool expired = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = (await db.Users.FindAsync(id))!;
        var row = await db.VerificationCodes.SingleAsync(c => c.UserId == id
            && c.Purpose == VerificationPurpose.EmailChange && c.ConsumedAt == null);
        row.CodeHash = scope.ServiceProvider.GetRequiredService<AuthTokenService>().HashVerificationCode(user, VerificationPurpose.EmailChange, code);
        if (expired) row.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Request_keeps_verified_inbox_sessions_and_Finance_until_confirmation()
    {
        var (client, captured, user) = await Account();
        var grant = await client.PostAsJsonAsync("/api/account/finance-tokens", new { currentPassword = Password });
        grant.EnsureSuccessStatusCode();
        var token = (await grant.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        using var reader = factory.CreateClient(); reader.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var email = NewEmail(); (await Request(client, email)).EnsureSuccessStatusCode();
        var account = await client.GetFromJsonAsync<JsonElement>("/api/account");
        Assert.Equal(user.Email, account.GetProperty("email").GetString()); Assert.True(account.GetProperty("emailVerified").GetBoolean());
        Assert.Equal(email, account.GetProperty("pendingEmail").GetString()); Assert.NotEqual(JsonValueKind.Null, account.GetProperty("pendingEmailExpiresAt").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await captured.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/integrations/finance/subscriptions")).StatusCode);
        await KnownCode(user.Id);
        (await client.PostAsJsonAsync("/api/account/confirm-email-change", new { code = "123456" })).EnsureSuccessStatusCode();
        account = await client.GetFromJsonAsync<JsonElement>("/api/account");
        Assert.Equal(email, account.GetProperty("email").GetString()); Assert.True(account.GetProperty("emailVerified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, account.GetProperty("pendingEmail").ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await captured.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/api/integrations/finance/subscriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/account/confirm-email-change", new { code = "123456" })).StatusCode);
        // A deliberately revoked old refresh must not revoke the new session.
        Assert.Equal(HttpStatusCode.Unauthorized, (await captured.PostAsync("/api/auth/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData(false, "000000")]
    [InlineData(true, "123456")]
    public async Task Wrong_or_expired_code_preserves_the_current_inbox(bool expired, string code)
    {
        var (client, _, user) = await Account(); (await Request(client, NewEmail())).EnsureSuccessStatusCode();
        await KnownCode(user.Id, expired: expired);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/account/confirm-email-change", new { code })).StatusCode);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(user.Email, (await db.Users.FindAsync(user.Id))!.Email);
        if (!expired) Assert.Equal(1, (await db.VerificationCodes.SingleAsync(c => c.UserId == user.Id)).Attempts);
    }

    [Fact]
    public async Task Cancellation_invalidates_pending_code_and_preserves_current_session()
    {
        var (client, captured, user) = await Account(); (await Request(client, NewEmail())).EnsureSuccessStatusCode(); await KnownCode(user.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/account/pending-email")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/account/confirm-email-change", new { code = "123456" })).StatusCode);
        var account = await client.GetFromJsonAsync<JsonElement>("/api/account");
        Assert.Equal(user.Email, account.GetProperty("email").GetString()); Assert.Equal(JsonValueKind.Null, account.GetProperty("pendingEmail").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await captured.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Pending_code_cannot_be_used_for_anonymous_login_or_password_reset()
    {
        var (client, _, user) = await Account(); (await Request(client, NewEmail())).EnsureSuccessStatusCode(); await KnownCode(user.Id);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/auth/verify-email", new { user.Username, code = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/auth/reset-password",
            new { user.Email, code = "123456", newPassword = "attacker-reset-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Throttled_change_does_not_replace_the_target_or_invalidate_its_code()
    {
        var (client, _, user) = await Account(); var first = NewEmail();
        (await Request(client, first)).EnsureSuccessStatusCode(); await KnownCode(user.Id);
        var throttled = await Request(client, NewEmail()); Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);
        var account = await client.GetFromJsonAsync<JsonElement>("/api/account"); Assert.Equal(first, account.GetProperty("pendingEmail").GetString());
        (await client.PostAsJsonAsync("/api/account/confirm-email-change", new { code = "123456" })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Taken_address_at_confirmation_preserves_original_inbox()
    {
        var (client, _, user) = await Account(); var email = NewEmail();
        (await Request(client, email)).EnsureSuccessStatusCode(); await KnownCode(user.Id);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User { Username = "taken_" + Guid.NewGuid().ToString("N"), Email = email, PasswordHash = "synthetic" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/account/confirm-email-change", new { code = "123456" })).StatusCode);
        Assert.Equal(user.Email, (await client.GetFromJsonAsync<JsonElement>("/api/account")).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Password_recovery_responses_are_identical_for_sent_throttled_and_unknown_accounts()
    {
        var (_, _, user) = await Account(); using var anonymous = factory.CreateClient();
        var sent = await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new { user.Email });
        var throttled = await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new { user.Email });
        var unknown = await anonymous.PostAsJsonAsync("/api/auth/forgot-password", new { email = NewEmail() });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode); Assert.Equal(sent.StatusCode, throttled.StatusCode); Assert.Equal(sent.StatusCode, unknown.StatusCode);
        Assert.Equal(await sent.Content.ReadAsStringAsync(), await throttled.Content.ReadAsStringAsync());
        Assert.Equal(await sent.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().VerificationCodes.CountAsync(c => c.UserId == user.Id));
    }

    [PostgresFact]
    public async Task Parallel_requests_from_separate_contexts_share_one_persistent_email_slot()
    {
        var (_, _, user) = await Account();
        var tasks = Enumerable.Range(0, 6).Select(async _ =>
        {
            using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loaded = (await db.Users.FindAsync(user.Id))!;
            return await scope.ServiceProvider.GetRequiredService<VerificationService>().IssueAsync(loaded, VerificationPurpose.PasswordReset);
        });
        var results = await Task.WhenAll(tasks); Assert.Equal(1, results.Count(r => r.Sent));
        Assert.All(results.Where(r => !r.Sent), result => Assert.InRange(result.RetryAfterSeconds, 1, 60));
        using var check = factory.Services.CreateScope();
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<AppDbContext>().VerificationCodes.CountAsync(c => c.UserId == user.Id));
    }

    [PostgresFact]
    public async Task Two_accounts_cannot_confirm_the_same_inbox_concurrently()
    {
        var (first, _, a) = await Account(); var (second, _, b) = await Account(); var email = NewEmail();
        (await Request(first, email)).EnsureSuccessStatusCode(); (await Request(second, email)).EnsureSuccessStatusCode();
        await KnownCode(a.Id); await KnownCode(b.Id);
        var responses = await Task.WhenAll(first.PostAsJsonAsync("/api/account/confirm-email-change", new { code = "123456" }),
            second.PostAsJsonAsync("/api/account/confirm-email-change", new { code = "123456" }));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync(u => u.Email == email));
    }
}
