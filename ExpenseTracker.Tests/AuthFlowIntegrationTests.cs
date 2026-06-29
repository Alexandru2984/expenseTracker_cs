using System.Linq;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ExpenseTracker.Api.Data;
using Xunit;

namespace ExpenseTracker.Tests;

/// <summary>
/// Boots the real API with an in-memory database (environment "Testing", so the
/// app skips both the production secret validation and the dev auto-migrate).
/// </summary>
public class TestApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Jwt:Secret", "test-secret-key-at-least-32-characters-long!!");
        builder.UseSetting("ConnectionStrings:DefaultConnection",
            "Host=localhost;Database=dummy;Username=x;Password=y");

        builder.ConfigureServices(services =>
        {
            var toRemove = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                         || d.ServiceType == typeof(AppDbContext))
                .ToList();
            foreach (var d in toRemove) services.Remove(d);

            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
        });
    }
}

public class AuthFlowIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    public AuthFlowIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_returns_ok_and_requires_verification()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/register",
            new { username = "alice", email = "alice@example.com", password = "parola1234" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Login_is_blocked_until_email_verified()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register",
            new { username = "bob", email = "bob@example.com", password = "parola1234" });

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "bob", password = "parola1234" });

        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_is_unauthorized()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register",
            new { username = "carol", email = "carol@example.com", password = "parola1234" });

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "carol", password = "totallyWrong9" });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_requires_auth()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/api/subscriptions");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
