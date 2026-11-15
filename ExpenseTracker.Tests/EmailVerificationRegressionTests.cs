using System.Net;
using System.Net.Http.Json;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

[Collection("Api")]
public class EmailVerificationRegressionTests(TestApiFactory factory)
{
    private async Task<User> Seed(bool verified, bool expired = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Username = "verification_" + Guid.NewGuid().ToString("N"),
            EmailVerified = verified,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("regression-password")
        };
        db.Users.Add(user);
        db.VerificationCodes.Add(new VerificationCode
        {
            UserId = user.Id,
            CodeHash = AuthTokenService.Hash("123456"),
            ExpiresAt = DateTime.UtcNow.AddMinutes(expired ? -1 : 15)
        });
        await db.SaveChangesAsync();
        return user;
    }

    [Theory]
    [InlineData("000000")]
    [InlineData("123456")]
    public async Task Verified_account_cannot_obtain_a_session_from_verification_endpoint(string code)
    {
        var user = await Seed(verified: true);
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/verify-email", new { user.Username, code });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .RefreshTokens.AnyAsync(t => t.UserId == user.Id));
    }

    [Fact]
    public async Task Verification_authenticates_once_and_replay_from_a_new_browser_fails()
    {
        var user = await Seed(verified: false);
        using var client = factory.CreateClient();
        var payload = new { user.Username, code = "123456" };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/verify-email", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var attacker = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await attacker.PostAsJsonAsync("/api/auth/verify-email", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData(false, "000000")]
    [InlineData(true, "123456")]
    public async Task Wrong_or_expired_code_cannot_authenticate(bool expired, string code)
    {
        var user = await Seed(verified: false, expired: expired);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/verify-email", new { user.Username, code })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}
