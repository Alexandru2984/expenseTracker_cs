using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ExpenseTracker.Api.Infrastructure;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Xunit;

namespace ExpenseTracker.Tests;

public class CsvTests
{
    [Fact]
    public void Field_escapes_embedded_quotes()
        => Assert.Equal("\"a\"\"b\"", Csv.Field("a\"b"));

    [Theory]
    [InlineData("=SUM(A1)")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@cmd")]
    public void Field_neutralizes_formula_injection(string dangerous)
        => Assert.StartsWith("\"'", Csv.Field(dangerous));

    [Fact]
    public void Field_leaves_safe_values_unprefixed()
        => Assert.Equal("\"Netflix\"", Csv.Field("Netflix"));

    [Fact]
    public void Parse_handles_quoted_commas()
    {
        var rows = Csv.Parse("a,\"b,c\",d");
        Assert.Equal(new[] { "a", "b,c", "d" }, rows[0]);
    }

    [Fact]
    public void Parse_handles_escaped_quotes()
    {
        var rows = Csv.Parse("\"a\"\"b\"");
        Assert.Equal("a\"b", rows[0][0]);
    }

    [Fact]
    public void Parse_splits_rows_and_drops_blank_lines()
    {
        var rows = Csv.Parse("Name,Cost\nNetflix,45\n\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Netflix", rows[1][0]);
    }
}

public class AuthTokenServiceTests
{
    private static AuthTokenService Svc()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-key-at-least-32-characters-long!!"
            })
            .Build();
        return new AuthTokenService(config);
    }

    [Fact]
    public void Hash_is_deterministic()
        => Assert.Equal(AuthTokenService.Hash("token"), AuthTokenService.Hash("token"));

    [Fact]
    public void Hash_differs_for_different_input()
        => Assert.NotEqual(AuthTokenService.Hash("a"), AuthTokenService.Hash("b"));

    [Fact]
    public void RefreshToken_raw_differs_from_stored_hash()
    {
        var (raw, hash, expires) = Svc().CreateRefreshToken();
        Assert.NotEqual(raw, hash);
        Assert.Equal(AuthTokenService.Hash(raw), hash);
        Assert.True(expires > DateTime.UtcNow);
    }

    [Fact]
    public void AccessToken_is_a_three_part_jwt()
    {
        var (token, _) = Svc().CreateAccessToken(new User { Id = Guid.NewGuid(), Username = "u" }, Guid.NewGuid());
        Assert.Equal(3, token.Split('.').Length);
    }

    [Fact]
    public void Verification_code_MAC_is_bound_to_account_inbox_and_purpose()
    {
        var service = Svc();
        var user = new User { Email = "original@example.invalid" };
        var hash = service.HashVerificationCode(user, VerificationPurpose.EmailVerification, "123456");
        Assert.StartsWith("v2:", hash);
        Assert.NotEqual(hash, AuthTokenService.Hash("123456"));
        Assert.NotEqual(hash, service.HashVerificationCode(user, VerificationPurpose.PasswordReset, "123456"));
        Assert.NotEqual(hash, service.HashVerificationCode(new User { Email=user.Email }, VerificationPurpose.EmailVerification, "123456"));
        user.Email = "new@example.invalid";
        Assert.NotEqual(hash, service.HashVerificationCode(user, VerificationPurpose.EmailVerification, "123456"));
    }

    [Fact]
    public void Password_verification_enforces_BCrypt_limit_by_UTF8_bytes()
    {
        var password = new string('é', 36); // 72 UTF-8 bytes
        var hash = BCrypt.Net.BCrypt.HashPassword(password);
        Assert.True(PasswordRules.Verify(password, hash));
        Assert.False(PasswordRules.Verify(password + "é", hash));
    }
}

public class ClientIpTests
{
    [Fact]
    public void Ignores_unverified_cloudflare_and_forwarded_headers()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["CF-Connecting-IP"] = "1.2.3.4";
        ctx.Request.Headers["X-Forwarded-For"] = "5.6.7.8";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        Assert.Equal("10.0.0.1", ClientIp.Partition(ctx));
    }

    [Fact]
    public void Falls_back_to_remote_ip()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        Assert.Equal("10.0.0.1", ClientIp.Partition(ctx));
    }
}
