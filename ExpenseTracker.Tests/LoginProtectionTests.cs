using System.Globalization;
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

[Collection("Api")]
public class LoginProtectionTests(TestApiFactory factory)
{
    private const string Password = "login-protection-password";
    private async Task<User> Account()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { Username = "LOGIN_" + Guid.NewGuid().ToString("N"),
            Email = Guid.NewGuid().ToString("N") + "@example.invalid", EmailVerified = true,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password) };
        db.Users.Add(user); await db.SaveChangesAsync(); return user;
    }
    private async Task<User> Read(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == id));
    }
    private async Task Block(User user)
    {
        for (var i = 0; i < 5; i++)
        {
            using var client = factory.CreateClient();
            var result = await client.PostAsJsonAsync("/api/auth/login", new { username = user.Username.ToLowerInvariant(), password = "incorrect-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        }
    }

    [Fact]
    public async Task Failure_budget_is_shared_across_clients_and_username_case_without_extending_the_block()
    {
        var user = await Account();
        using var existing = factory.CreateClient();
        (await existing.PostAsJsonAsync("/api/auth/login", new { user.Username, password = Password })).EnsureSuccessStatusCode();
        await Block(user); var before = await Read(user.Id);
        Assert.Equal(5, before.LoginFailureCount); Assert.True(before.LoginBlockedUntil > DateTime.UtcNow);
        using var blocked = factory.CreateClient();
        var result = await blocked.PostAsJsonAsync("/api/auth/login", new { user.Username, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.DoesNotContain(result.Headers.GetValues("Set-Cookie"), c => c.StartsWith("access_token=") || c.StartsWith("refresh_token="));
        var after = await Read(user.Id);
        Assert.Equal(before.LoginFailureCount, after.LoginFailureCount);
        Assert.Equal(before.LoginBlockedUntil, after.LoginBlockedUntil);
        Assert.Equal(before.LastLoginFailureAt, after.LastLoginFailureAt);
        Assert.Equal(HttpStatusCode.OK, (await existing.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Unknown_wrong_and_blocked_logins_have_the_same_public_error()
    {
        var user = await Account(); using var client = factory.CreateClient();
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { username = Guid.NewGuid().ToString("N"), password = Password });
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { user.Username, password = "incorrect-password" });
        await Block(user);
        var blocked = await client.PostAsJsonAsync("/api/auth/login", new { user.Username, password = Password });
        var bodies = new List<JsonElement>();
        foreach (var response in new[] { unknown, wrong, blocked })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.RetryAfter);
            bodies.Add(await response.Content.ReadFromJsonAsync<JsonElement>());
        }
        Assert.All(bodies, body => Assert.Equal(bodies[0].GetProperty("detail").GetString(), body.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Expired_backoff_allows_a_valid_login_and_resets_failures()
    {
        var user = await Account(); await Block(user);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var row = (await db.Users.FindAsync(user.Id))!;
            row.LoginBlockedUntil = DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { user.Username, password = Password })).EnsureSuccessStatusCode();
        var after = await Read(user.Id);
        Assert.Equal(0, after.LoginFailureCount); Assert.Null(after.LoginBlockedUntil); Assert.Null(after.LastLoginFailureAt);
    }

    [Fact]
    public async Task Verified_password_recovery_unlocks_a_blocked_account()
    {
        var user = await Account(); await Block(user);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.VerificationCodes.Add(new VerificationCode { UserId = user.Id, Purpose = VerificationPurpose.PasswordReset,
                CodeHash = scope.ServiceProvider.GetRequiredService<AuthTokenService>().HashVerificationCode(user, VerificationPurpose.PasswordReset, "123456"),
                ExpiresAt = DateTime.UtcNow.AddMinutes(10) }); await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        var invalid = await client.PostAsJsonAsync("/api/auth/reset-password", new { user.Email, code = "000000", newPassword = Password + "2" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.NotNull((await Read(user.Id)).LoginBlockedUntil);
        (await client.PostAsJsonAsync("/api/auth/reset-password", new { user.Email, code = "123456", newPassword = Password + "2" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/auth/login", new { user.Username, password = Password + "2" })).EnsureSuccessStatusCode();
        Assert.Equal(0, (await Read(user.Id)).LoginFailureCount);
    }

    [Fact]
    public async Task Username_lookup_does_not_follow_the_request_thread_culture()
    {
        var user = await Account(); using var client = factory.CreateClient(); var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            (await client.PostAsJsonAsync("/api/auth/login", new { user.Username, password = Password })).EnsureSuccessStatusCode();
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Theory]
    [InlineData("  a ")]
    [InlineData("abc\nxyz")]
    public async Task Registration_rejects_trimmed_short_or_control_character_usernames(string username)
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", new { username, email = "unused@example.invalid", password = Password })).StatusCode);
    }

    [PostgresFact]
    public async Task Non_ASCII_username_lookup_uses_the_database_case_mapping_for_both_operands()
    {
        var user = await Account();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = (await db.Users.FindAsync(user.Id))!;
            user.Username = stored.Username = "İ" + user.Username;
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { user.Username, password = Password })).EnsureSuccessStatusCode();
    }

    [PostgresFact]
    public async Task Database_unique_index_rejects_case_collisions_even_without_the_API_precheck()
    {
        var user = await Account(); using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User { Username = user.Username.ToLowerInvariant(), PasswordHash = user.PasswordHash });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<Npgsql.PostgresException>(error.InnerException);
        Assert.Equal("23505", postgres.SqlState); Assert.Equal("IX_Users_Username_CaseInsensitive", postgres.ConstraintName);
    }

    [PostgresFact]
    public async Task Concurrent_registrations_cannot_create_two_case_variants()
    {
        var name = "RACE_" + Guid.NewGuid().ToString("N");
        using var first = factory.CreateClient(); using var second = factory.CreateClient();
        var responses = await Task.WhenAll(
            first.PostAsJsonAsync("/api/auth/register", new { username = name, email = Guid.NewGuid() + "@example.invalid", password = Password }),
            second.PostAsJsonAsync("/api/auth/register", new { username = name.ToLowerInvariant(), email = Guid.NewGuid() + "@example.invalid", password = Password }));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [PostgresFact]
    public async Task Concurrent_password_failures_cannot_exceed_the_unblocked_attempt_budget()
    {
        var user = await Account();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var client = factory.CreateClient();
            return await client.PostAsJsonAsync("/api/auth/login", new { user.Username, password = "incorrect-password" });
        }));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Equal(5, (await Read(user.Id)).LoginFailureCount);
    }
}

public class LoginBackoffPolicyTests
{
    [Fact]
    public void Backoff_grows_to_five_minutes_and_expires_after_fifteen_minutes_of_inactivity()
    {
        var user = new User(); var now = DateTime.UtcNow;
        for (var i = 1; i <= 12; i++)
        {
            LoginProtection.Fail(user, now);
            if (i < 5) Assert.Null(user.LoginBlockedUntil);
            else
            {
                var delay = (user.LoginBlockedUntil!.Value - now).TotalSeconds;
                Assert.InRange(delay, 30, 300);
                if (i >= 9) Assert.Equal(300, delay);
                now = user.LoginBlockedUntil.Value;
            }
        }
        LoginProtection.Fail(user, now.AddMinutes(16));
        Assert.Equal(1, user.LoginFailureCount); Assert.Null(user.LoginBlockedUntil);
    }
}
