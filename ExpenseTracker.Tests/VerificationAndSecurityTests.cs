using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Xunit;

namespace ExpenseTracker.Tests;

public class VerificationServiceTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static VerificationService Svc(AppDbContext db)
    {
        var email = new EmailService(new ConfigurationBuilder().Build(),
            NullLogger<EmailService>.Instance);
        return new VerificationService(db, email);
    }

    private static async Task<User> SeedUser(AppDbContext db)
    {
        var user = new User { Id = Guid.NewGuid(), Username = "u", Email = "u@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    // Inserts a code row directly with a known plaintext so we can exercise Consume.
    private static async Task SeedCode(AppDbContext db, Guid userId, string plaintext,
        VerificationPurpose purpose = VerificationPurpose.EmailVerification, int ttlMinutes = 15)
    {
        db.VerificationCodes.Add(new VerificationCode
        {
            UserId = userId,
            Purpose = purpose,
            CodeHash = AuthTokenService.Hash(plaintext),
            ExpiresAt = DateTime.UtcNow.AddMinutes(ttlMinutes)
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Consume_accepts_the_correct_code_once()
    {
        var db = NewDb();
        var user = await SeedUser(db);
        await SeedCode(db, user.Id, "123456");

        Assert.True(await Svc(db).ConsumeAsync(user, VerificationPurpose.EmailVerification, "123456"));
        // Second attempt fails: the code was consumed (single use).
        Assert.False(await Svc(db).ConsumeAsync(user, VerificationPurpose.EmailVerification, "123456"));
    }

    [Fact]
    public async Task Consume_rejects_a_wrong_code()
    {
        var db = NewDb();
        var user = await SeedUser(db);
        await SeedCode(db, user.Id, "123456");

        Assert.False(await Svc(db).ConsumeAsync(user, VerificationPurpose.EmailVerification, "000000"));
    }

    [Fact]
    public async Task Consume_rejects_an_expired_code()
    {
        var db = NewDb();
        var user = await SeedUser(db);
        await SeedCode(db, user.Id, "123456", ttlMinutes: -1);

        Assert.False(await Svc(db).ConsumeAsync(user, VerificationPurpose.EmailVerification, "123456"));
    }

    [Fact]
    public async Task Consume_burns_the_code_after_too_many_attempts()
    {
        var db = NewDb();
        var user = await SeedUser(db);
        await SeedCode(db, user.Id, "123456");
        var svc = Svc(db);

        // Five wrong tries are allowed; the sixth burns the code.
        for (int i = 0; i < 5; i++)
            Assert.False(await svc.ConsumeAsync(user, VerificationPurpose.EmailVerification, "000000"));
        Assert.False(await svc.ConsumeAsync(user, VerificationPurpose.EmailVerification, "000000"));

        // Even the correct code no longer works once burned.
        Assert.False(await svc.ConsumeAsync(user, VerificationPurpose.EmailVerification, "123456"));
    }

    [Fact]
    public async Task Issue_invalidates_prior_unconsumed_codes()
    {
        var db = NewDb();
        var user = await SeedUser(db);

        await Svc(db).IssueAsync(user, VerificationPurpose.EmailVerification);
        await Svc(db).IssueAsync(user, VerificationPurpose.EmailVerification);

        var unconsumed = await db.VerificationCodes
            .CountAsync(c => c.UserId == user.Id && c.ConsumedAt == null);
        Assert.Equal(1, unconsumed);
    }
}

[Collection("Api")]
public class CsrfEnforcementTests
{
    private readonly TestApiFactory _factory;
    public CsrfEnforcementTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Request_bootstraps_a_readable_csrf_cookie()
    {
        var client = _factory.CreateClient();
        // An anonymous request that reaches the CSRF middleware (here an unmatched
        // route, which passes authorization) gets a token issued, so the SPA
        // always has a value to echo back on later unsafe calls.
        var res = await client.GetAsync("/csrf-probe");

        var setCookies = res.Headers.TryGetValues("Set-Cookie", out var values)
            ? string.Join("; ", values) : "";
        Assert.Contains("csrf_token=", setCookies);
    }

    [Fact]
    public async Task Refresh_without_cookie_is_unauthorized()
    {
        var client = _factory.CreateClient();
        // Auth endpoints are CSRF-exempt, so this reaches the handler and 401s.
        var res = await client.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
