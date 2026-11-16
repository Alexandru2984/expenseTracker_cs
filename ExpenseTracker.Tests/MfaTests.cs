using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Integrations;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ExpenseTracker.Tests;

[Collection("Api")]
public class MfaTests(TestApiFactory factory)
{
    private const string Password="authenticator-test-password";
    public const string TestKey="AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private WebApplicationFactory<Program> App() => factory.WithWebHostBuilder(b=>b.UseSetting("Mfa:EncryptionKey",TestKey));
    private static async Task<(HttpClient client,Guid id)> Account(WebApplicationFactory<Program> app)
    {
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user=new User {Username="mfa_"+Guid.NewGuid().ToString("N"),Email=Guid.NewGuid().ToString("N")+"@example.invalid",
            EmailVerified=true,PasswordHash=PasswordRules.Hash(Password,false)};
        var session=new RefreshToken {UserId=user.Id,TokenHash=AuthTokenService.Hash(Guid.NewGuid().ToString()),ExpiresAt=DateTime.UtcNow.AddDays(1)};
        db.Users.Add(user);db.RefreshTokens.Add(session);await db.SaveChangesAsync();
        var jwt=scope.ServiceProvider.GetRequiredService<AuthTokenService>().CreateAccessToken(user,session.Id).token;
        var client=app.CreateClient(new WebApplicationFactoryClientOptions {HandleCookies=false});
        client.DefaultRequestHeaders.Add("Cookie",$"access_token={jwt}; csrf_token=mfa-test-csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-Token","mfa-test-csrf");
        return(client,user.Id);
    }
    private static void AdoptCookies(HttpClient client,HttpResponseMessage response)
    {
        var parts=response.Headers.GetValues("Set-Cookie").Where(c=>!c.Contains("expires=Thu, 01 Jan 1970",StringComparison.OrdinalIgnoreCase))
            .Select(c=>c.Split(';')[0]).Where(c=>c.StartsWith("access_token=")||c.StartsWith("refresh_token=")).Append("csrf_token=mfa-test-csrf");
        client.DefaultRequestHeaders.Remove("Cookie");client.DefaultRequestHeaders.Add("Cookie",string.Join("; ",parts));
    }
    private static async Task<JsonElement> Begin(HttpClient client)
    {
        var response=await client.PostAsJsonAsync("/api/account/mfa/setup",new {currentPassword=Password});response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static string Code(string secret,int delta=0)=>Totp.Code(secret,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30+delta);
    private static async Task<(string secret,string[] codes)> Enable(HttpClient client)
    {
        var setup=await Begin(client);var secret=setup.GetProperty("secret").GetString()!;
        var response=await client.PostAsJsonAsync("/api/account/mfa/confirm",new {setupId=setup.GetProperty("setupId").GetGuid(),code=Code(secret)});
        response.EnsureSuccessStatusCode();AdoptCookies(client,response);
        return(secret,(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").EnumerateArray().Select(c=>c.GetString()!).ToArray());
    }
    private static async Task<(HttpClient client,string challenge)> Challenge(WebApplicationFactory<Program> app,string username)
    {
        var client=app.CreateClient(new WebApplicationFactoryClientOptions {HandleCookies=false});
        var response=await client.PostAsJsonAsync("/api/auth/login",new {username,password=Password});
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        if(response.Headers.TryGetValues("Set-Cookie",out var cookies))
            Assert.DoesNotContain(cookies,c=>c.StartsWith("access_token=")||c.StartsWith("refresh_token="));
        return(client,(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("challengeToken").GetString()!);
    }

    [Fact]
    public async Task Setup_is_password_protected_encrypted_user_bound_and_does_not_enable_mfa_until_confirmed()
    {
        using var app=App();var (client,id)=await Account(app);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/account/mfa/setup",new {currentPassword="wrong"})).StatusCode);
        var setup=await Begin(client);var secret=setup.GetProperty("secret").GetString()!;
        Assert.Matches("^[A-Z2-7]{32}$",secret);Assert.StartsWith("otpauth://totp/",setup.GetProperty("otpauthUri").GetString());
        var status=await client.GetFromJsonAsync<JsonElement>("/api/account/mfa");Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain(secret,status.ToString());
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user=await db.Users.AsNoTracking().SingleAsync(u=>u.Id==id);
        Assert.NotEqual(secret,user.MfaPendingSecret);Assert.StartsWith("v1.",user.MfaPendingSecret);
        Assert.Equal(secret,scope.ServiceProvider.GetRequiredService<MfaSecrets>().Unprotect(id,user.MfaPendingSecret!));
        Assert.Equal(HttpStatusCode.TooManyRequests,(await client.PostAsJsonAsync("/api/account/mfa/setup",new {currentPassword=Password})).StatusCode);
        (await client.DeleteAsync("/api/account/mfa/setup?setupId="+setup.GetProperty("setupId"))).EnsureSuccessStatusCode();
        Assert.Null((await client.GetFromJsonAsync<JsonElement>("/api/account/mfa")).GetProperty("pendingSetupId").GetString());
    }

    [Fact]
    public async Task Enabling_revokes_old_browser_sessions_but_preserves_scoped_finance_exports()
    {
        using var app=App();var (client,id)=await Account(app);
        using var old=app.CreateClient(new WebApplicationFactoryClientOptions {HandleCookies=false});
        old.DefaultRequestHeaders.Add("Cookie",client.DefaultRequestHeaders.GetValues("Cookie"));
        string token;
        using(var scope=app.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();token=FinanceTokens.Create();
            db.RefreshTokens.Add(new RefreshToken {UserId=id,TokenHash=FinanceTokens.Hash(token),ExpiresAt=DateTime.UtcNow.AddDays(1)});
            await db.SaveChangesAsync();
        }
        var (secret,codes)=await Enable(client);Assert.Equal(10,codes.Length);Assert.Equal(10,codes.Distinct().Count());
        Assert.Equal(HttpStatusCode.Unauthorized,(await old.GetAsync("/api/auth/me")).StatusCode);
        (await client.GetAsync("/api/auth/me")).EnsureSuccessStatusCode();
        using var finance=app.CreateClient(new WebApplicationFactoryClientOptions {HandleCookies=false});finance.DefaultRequestHeaders.Authorization=new("Bearer",token);
        (await finance.GetAsync("/api/integrations/finance/subscriptions")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized,(await finance.GetAsync("/api/account/mfa")).StatusCode);
        using var check=app.Services.CreateScope();var dbCheck=check.ServiceProvider.GetRequiredService<AppDbContext>();
        var user=await dbCheck.Users.AsNoTracking().SingleAsync(u=>u.Id==id);
        Assert.True(user.MfaEnabled);Assert.StartsWith(PasswordRules.MfaPrefix,user.PasswordHash);
        var stored=await dbCheck.MfaRecoveryCodes.AsNoTracking().Where(c=>c.UserId==id).ToListAsync();
        Assert.All(stored,c=>Assert.DoesNotContain(c.CodeHash,codes));
        Assert.DoesNotContain(secret,(await client.GetStringAsync("/api/account/mfa")));
    }

    [Fact]
    public async Task Password_only_login_issues_no_session_and_totp_is_single_use_across_challenges()
    {
        using var app=App();var (owner,id)=await Account(app);var (secret,_)=await Enable(owner);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user=await db.Users.AsNoTracking().SingleAsync(u=>u.Id==id);
        var (first,challenge)=await Challenge(app,user.Username);
        // Enrollment consumed the current step. The adjacent allowed step is a fresh proof.
        var next=Code(secret,1);
        var response=await first.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=challenge,code=next});
        response.EnsureSuccessStatusCode();AdoptCookies(first,response);
        (await first.GetAsync("/api/auth/me")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized,(await first.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=challenge,code=next})).StatusCode);
        var (second,secondChallenge)=await Challenge(app,user.Username);
        Assert.Equal(HttpStatusCode.Unauthorized,(await second.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=secondChallenge,code=next})).StatusCode);
    }

    [Fact]
    public async Task Recovery_is_one_use_and_regeneration_invalidates_old_codes_and_disabling_requires_two_proofs()
    {
        using var app=App();var (owner,id)=await Account(app);var (_,codes)=await Enable(owner);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var username=(await db.Users.AsNoTracking().SingleAsync(u=>u.Id==id)).Username;
        var (client,challenge)=await Challenge(app,username);
        var login=await client.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=challenge,code=codes[0]});login.EnsureSuccessStatusCode();
        AdoptCookies(client,login);client.DefaultRequestHeaders.Add("X-CSRF-Token","mfa-test-csrf");
        Assert.Equal(9,(await client.GetFromJsonAsync<JsonElement>("/api/account/mfa")).GetProperty("recoveryCodesRemaining").GetInt32());
        var rotate=await client.PostAsJsonAsync("/api/account/mfa/recovery-codes",new {currentPassword=Password,code=codes[1]});rotate.EnsureSuccessStatusCode();
        var fresh=(await rotate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes")[0].GetString();
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/account/mfa/disable",new {currentPassword=Password,code=codes[2]})).StatusCode);
        var disable=await client.PostAsJsonAsync("/api/account/mfa/disable",new {currentPassword=Password,code=fresh});
        disable.EnsureSuccessStatusCode();AdoptCookies(client,disable);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/account/mfa")).GetProperty("enabled").GetBoolean());
        (await client.PostAsJsonAsync("/api/auth/login",new {username,password=Password})).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Bad_expired_and_replaced_challenges_or_pending_setups_cannot_authenticate()
    {
        using var app=App();var (owner,id)=await Account(app);var setup=await Begin(owner);
        for(var i=0;i<5;i++)
            Assert.Equal(HttpStatusCode.BadRequest,(await owner.PostAsJsonAsync("/api/account/mfa/confirm",new {setupId=setup.GetProperty("setupId").GetGuid(),code="not-a-code"})).StatusCode);
        // Model validation does not spend OTP attempts; a valid-format wrong code does.
        var secret=setup.GetProperty("secret").GetString()!;var wrong=Code(secret)=="000000"?"000001":"000000";
        for(var i=0;i<5;i++)await owner.PostAsJsonAsync("/api/account/mfa/confirm",new {setupId=setup.GetProperty("setupId").GetGuid(),code=wrong});
        Assert.Null((await owner.GetFromJsonAsync<JsonElement>("/api/account/mfa")).GetProperty("pendingSetupId").GetString());
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user=await db.Users.SingleAsync(u=>u.Id==id);user.MfaSetupStartedAt=DateTime.UtcNow.AddMinutes(-2);await db.SaveChangesAsync();
        await Enable(owner);
        var (client,challenge)=await Challenge(app,user.Username);
        var row=await db.MfaLoginChallenges.SingleAsync(c=>c.UserId==id);row.ExpiresAt=DateTime.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=challenge,code="000000"})).StatusCode);
    }

    [Fact]
    public async Task Mfa_setup_management_requires_csrf_and_matching_browser_account()
    {
        using var app=App();var (client,_)=await Account(app);var (other,_)=await Account(app);
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/account/mfa/setup",new {currentPassword=Password})).StatusCode);
        client.DefaultRequestHeaders.Add("X-CSRF-Token","mfa-test-csrf");
        client.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader,(await other.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("accountContext").GetString());
        Assert.Equal(HttpStatusCode.Conflict,(await client.GetAsync("/api/account/mfa")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/account/mfa/setup",new {currentPassword=Password})).StatusCode);
    }

    [PostgresFact]
    public async Task Recovery_code_has_one_winner_under_concurrent_logins()
    {
        using var app=App();var (owner,id)=await Account(app);var (_,codes)=await Enable(owner);
        using var scope=app.Services.CreateScope();var user=await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u=>u.Id==id);
        var (first,a)=await Challenge(app,user.Username);var (second,b)=await Challenge(app,user.Username);
        var responses=await Task.WhenAll(first.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=a,code=codes[0]}),
            second.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=b,code=codes[0]}));
        Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task Legacy_password_only_writes_cannot_remove_the_marker_while_mfa_is_enabled()
    {
        using var app=App();var (owner,id)=await Account(app);await Enable(owner);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user=await db.Users.SingleAsync(u=>u.Id==id);
        Assert.ThrowsAny<Exception>(()=>BCrypt.Net.BCrypt.Verify(Password,user.PasswordHash));
        user.PasswordHash=PasswordRules.Hash("legacy-password-reset",false);
        await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());
    }

    [Fact]
    public async Task Password_reset_preserves_mfa_and_cannot_issue_a_password_only_session()
    {
        using var app=App();var (owner,id)=await Account(app);await Enable(owner);string username,email;
        using(var scope=app.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();var user=await db.Users.SingleAsync(u=>u.Id==id);
            username=user.Username;email=user.Email!;
            db.VerificationCodes.Add(new VerificationCode {UserId=id,Purpose=VerificationPurpose.PasswordReset,
                CodeHash=scope.ServiceProvider.GetRequiredService<AuthTokenService>().HashVerificationCode(user,VerificationPurpose.PasswordReset,"123456"),
                ExpiresAt=DateTime.UtcNow.AddMinutes(10)});
            await db.SaveChangesAsync();
        }
        var response=await owner.PostAsJsonAsync("/api/auth/reset-password",new {email,code="123456",newPassword="reset-mfa-test-password"});
        response.EnsureSuccessStatusCode();
        using var fresh=app.CreateClient(new WebApplicationFactoryClientOptions {HandleCookies=false});
        Assert.Equal(HttpStatusCode.Accepted,(await fresh.PostAsJsonAsync("/api/auth/login",new {username,password="reset-mfa-test-password"})).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await owner.GetAsync("/api/auth/me")).StatusCode);
        using var check=app.Services.CreateScope();var saved=await check.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u=>u.Id==id);
        Assert.True(saved.MfaEnabled);Assert.StartsWith(PasswordRules.MfaPrefix,saved.PasswordHash);
    }

    [Fact]
    public async Task Recovery_codes_work_when_the_authenticator_key_cannot_be_decrypted_without_weakening_login()
    {
        using var app=App();var (owner,id)=await Account(app);var (secret,codes)=await Enable(owner);string username;
        using(var scope=app.Services.CreateScope())username=(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AsNoTracking().SingleAsync(u=>u.Id==id)).Username;
        using var changedKey=factory.WithWebHostBuilder(b=>b.UseSetting("Mfa:EncryptionKey",Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        var (client,challenge)=await Challenge(changedKey,username);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=challenge,code=Code(secret,1)})).StatusCode);
        var response=await client.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=challenge,code=codes[0]});
        response.EnsureSuccessStatusCode();AdoptCookies(client,response);client.DefaultRequestHeaders.Add("X-CSRF-Token","mfa-test-csrf");
        var disabled=await client.PostAsJsonAsync("/api/account/mfa/disable",new {currentPassword=Password,code=codes[1]});
        disabled.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Correct_password_does_not_reset_mfa_failures_and_canceled_challenges_cannot_be_used()
    {
        using var app=App();var (owner,id)=await Account(app);var (_,codes)=await Enable(owner);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user=await db.Users.SingleAsync(u=>u.Id==id);
        user.LoginFailureCount=3;user.LastLoginFailureAt=DateTime.UtcNow;await db.SaveChangesAsync();
        var (client,challenge)=await Challenge(app,user.Username);
        await db.Entry(user).ReloadAsync();Assert.Equal(3,user.LoginFailureCount);
        (await client.PostAsJsonAsync("/api/auth/mfa-cancel",new {challengeToken=challenge,code="cancel"})).EnsureSuccessStatusCode();
        var denied=await client.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=challenge,code=codes[0]});
        Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        Assert.Equal("mfa_restart_required",(await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var (_,next)=await Challenge(app,user.Username);
        var accepted=await client.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=next,code=codes[0]});
        accepted.EnsureSuccessStatusCode();await db.Entry(user).ReloadAsync();Assert.Equal(0,user.LoginFailureCount);
    }

    [Fact]
    public async Task Pending_challenges_are_bounded_and_canceling_releases_a_slot()
    {
        using var app=App();var (owner,id)=await Account(app);await Enable(owner);
        using var scope=app.Services.CreateScope();var username=(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(u=>u.Id==id)).Username;
        var (first,challenge)=await Challenge(app,username);await Challenge(app,username);await Challenge(app,username);
        Assert.Equal(HttpStatusCode.TooManyRequests,(await first.PostAsJsonAsync("/api/auth/login",new {username,password=Password})).StatusCode);
        (await first.PostAsJsonAsync("/api/auth/mfa-cancel",new {challengeToken=challenge,code="cancel"})).EnsureSuccessStatusCode();
        await Challenge(app,username);
    }

    [Fact]
    public async Task Missing_key_disables_enrollment_and_never_falls_back_to_password_only_login()
    {
        using var app=App();var (owner,id)=await Account(app);var (_,codes)=await Enable(owner);string username;
        using(var scope=app.Services.CreateScope())username=(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.SingleAsync(u=>u.Id==id)).Username;
        using var missing=factory.WithWebHostBuilder(b=>b.UseSetting("Mfa:EncryptionKey",""));
        var (client,challenge)=await Challenge(missing,username);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=challenge,code="123456"})).StatusCode);
        var recovered=await client.PostAsJsonAsync("/api/auth/mfa-login",new {challengeToken=challenge,code=codes[0]});recovered.EnsureSuccessStatusCode();
        AdoptCookies(client,recovered);client.DefaultRequestHeaders.Add("X-CSRF-Token","mfa-test-csrf");
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/account/mfa")).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await client.PostAsJsonAsync("/api/account/mfa/setup",new {currentPassword=Password})).StatusCode);
    }

    [PostgresFact]
    public async Task Backup_verifier_accepts_the_correct_key_and_rejects_a_wrong_key_without_printing_secrets()
    {
        using var app=App();var (owner,_)=await Account(app);await Enable(owner);
        using var scope=app.Services.CreateScope();var connection=scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString();
        IConfiguration Config(string key)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            {"ConnectionStrings:DefaultConnection",connection},{"Mfa:EncryptionKey",key}}).Build();
        Assert.Equal(0,await MfaBackupProbe.Run(Config(TestKey)));
        Assert.Equal(1,await MfaBackupProbe.Run(Config(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))));
    }
}

public class TotpTests
{
    [Theory]
    [InlineData(59L,"94287082")] [InlineData(1111111109L,"07081804")] [InlineData(1111111111L,"14050471")]
    [InlineData(1234567890L,"89005924")] [InlineData(2000000000L,"69279037")] [InlineData(20000000000L,"65353130")]
    public void Rfc6238_sha1_vectors_match(long time,string expected)
    {
        var secret=Totp.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));
        Assert.Equal(expected,Totp.Code(secret,time/30,8));
    }
    [Fact]
    public void Encryption_authenticates_user_ciphertext_and_key_and_recovery_hashes_have_their_own_namespace()
    {
        MfaSecrets Service(string key)=>new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Mfa:EncryptionKey",key}}).Build());
        var crypto=Service(MfaTests.TestKey);var id=Guid.NewGuid();var secret=Totp.NewSecret();var encrypted=crypto.Protect(id,secret);
        Assert.Equal(secret,crypto.Unprotect(id,encrypted));Assert.NotEqual(encrypted,crypto.Protect(id,secret));
        Assert.ThrowsAny<CryptographicException>(()=>crypto.Unprotect(Guid.NewGuid(),encrypted));
        Assert.ThrowsAny<CryptographicException>(()=>Service(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))).Unprotect(id,encrypted));
        Assert.NotEqual(MfaSecrets.RecoveryHash(id,"AAAAA-BBBBB-CCCCC-DDDDD"),MfaSecrets.RecoveryHash(Guid.NewGuid(),"AAAAA-BBBBB-CCCCC-DDDDD"));
        Assert.Equal(MfaSecrets.RecoveryHash(id,"AAAAA-BBBBB-CCCCC-DDDDD"),MfaSecrets.RecoveryHash(id,"aaaaa bbbbb ccccc ddddd"));
    }
}
