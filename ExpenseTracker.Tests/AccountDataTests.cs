using System.Net;
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
public class AccountDataTests(TestApiFactory factory)
{
    private const string Password="account-data-synthetic-password";
    private WebApplicationFactory<Program> App()=>factory.WithWebHostBuilder(b=>b.UseSetting("Mfa:EncryptionKey",MfaTests.TestKey));
    private sealed record Owner(HttpClient Client,Guid Id,string Username,Guid SessionId,string Finance,string Recovery);
    private static async Task<Owner> Seed(WebApplicationFactory<Program> app,bool mfa=false)
    {
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user=new User{Username="data_"+Guid.NewGuid().ToString("N"),Email=Guid.NewGuid().ToString("N")+"@example.invalid",EmailVerified=true,
            PasswordHash=PasswordRules.Hash(Password,mfa),MfaEnabled=mfa,MfaEpoch=mfa?Guid.NewGuid():Guid.Empty};
        if(mfa)user.MfaSecret=scope.ServiceProvider.GetRequiredService<MfaSecrets>().Protect(user.Id,Totp.NewSecret());
        var session=new RefreshToken{UserId=user.Id,TokenHash=AuthTokenService.Hash(Guid.NewGuid().ToString()),ExpiresAt=DateTime.UtcNow.AddDays(1)};
        var finance=FinanceTokens.Create();var recovery=MfaSecrets.NewRecovery();
        var subscription=new SubscriptionItem{UserId=user.Id,Name="private-"+user.Id,Cost=12,Currency="EUR",NextBillingDate=DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)),Notes="private notes <script>not HTML</script>",ProviderUrl="https://example.invalid/account",TrialEndsOn=DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1))};
        var goal=new SavingsGoal{UserId=user.Id,Name="private-goal",TargetAmount=500,Currency="EUR"};
        db.Users.Add(user);db.RefreshTokens.AddRange(session,new RefreshToken{UserId=user.Id,TokenHash=FinanceTokens.Hash(finance),ExpiresAt=DateTime.UtcNow.AddDays(1)});
        db.Subscriptions.Add(subscription);db.SavingsGoals.Add(goal);
        db.GoalContributions.Add(new GoalContribution{GoalId=goal.Id,Id=Guid.NewGuid(),Amount=10,ContributedOn=DateOnly.FromDateTime(DateTime.UtcNow),Note="private contribution"});
        db.Budgets.Add(new MonthlyBudget{UserId=user.Id,Currency="EUR",MonthlyLimit=100});
        db.SubscriptionPriceChanges.Add(new SubscriptionPriceChange{SubscriptionId=subscription.Id,ChangedAt=DateTime.UtcNow,PreviousCost=10,Cost=12,PreviousCurrency="EUR",Currency="EUR"});
        db.NotificationPreferences.Add(new NotificationPreference{UserId=user.Id});
        db.NotificationOutbox.Add(new NotificationOutbox{UserId=user.Id,LocalDate=DateOnly.FromDateTime(DateTime.UtcNow),RecipientHash="private-recipient-hash",State=NotificationState.Pending});
        db.VerificationCodes.Add(new VerificationCode{UserId=user.Id,CodeHash="private-email-code-hash",ExpiresAt=DateTime.UtcNow.AddMinutes(15)});
        db.MfaRecoveryCodes.Add(new MfaRecoveryCode{UserId=user.Id,CodeHash=MfaSecrets.RecoveryHash(user.Id,recovery)});
        db.MfaLoginChallenges.Add(new MfaLoginChallenge{UserId=user.Id,TokenHash=Guid.NewGuid().ToString("N").PadRight(64,'A'),CredentialStamp="private-stamp",ExpiresAt=DateTime.UtcNow.AddMinutes(5)});
        await db.SaveChangesAsync();
        var tokens=scope.ServiceProvider.GetRequiredService<AuthTokenService>();
        var client=app.CreateClient(new WebApplicationFactoryClientOptions{HandleCookies=false});
        client.DefaultRequestHeaders.Add("Cookie",$"access_token={tokens.CreateAccessToken(user,session.Id).token}; csrf_token=data-test-csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-Token","data-test-csrf");client.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader,tokens.AccountContext(user.Id));
        return new(client,user.Id,user.Username,session.Id,finance,recovery);
    }
    private static object Proof(Owner owner,string password=Password,string? confirmation=null)=>new {currentPassword=password,code=owner.Recovery,confirmationUsername=confirmation??owner.Username};

    [Fact]
    public async Task Archive_is_complete_account_scoped_machine_readable_and_contains_no_authentication_secrets()
    {
        using var app=App();var a=await Seed(app,true);var b=await Seed(app);
        var response=await a.Client.PostAsJsonAsync("/api/account/data/export",Proof(a));response.EnsureSuccessStatusCode();
        Assert.Equal("application/json",response.Content.Headers.ContentType?.MediaType);Assert.Equal("attachment",response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("no-store",response.Headers.CacheControl?.ToString());
        var text=await response.Content.ReadAsStringAsync();using var json=JsonDocument.Parse(text);var root=json.RootElement;
        Assert.Equal("expense-tracker-account",root.GetProperty("format").GetString());Assert.Equal(1,root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(a.Username,root.GetProperty("profile").GetProperty("username").GetString());
        foreach(var name in new[]{"subscriptions","priceHistory","budgets","goals","contributions","notificationPreferences","notificationHistory"})Assert.Single(root.GetProperty(name).EnumerateArray());
        Assert.Equal(2,root.GetProperty("accessHistory").GetArrayLength());
        Assert.Equal("Monthly",root.GetProperty("subscriptions")[0].GetProperty("billingPeriod").GetString());
        Assert.Equal("private notes <script>not HTML</script>",root.GetProperty("subscriptions")[0].GetProperty("notes").GetString());
        foreach(var forbidden in new[]{b.Username,b.Id.ToString(),Password,a.Finance,a.Recovery,"passwordHash","tokenHash","mfaSecret","mfaEpoch","codeHash","recipientHash","private-recipient-hash","private-email-code-hash","private-stamp"})
            Assert.DoesNotContain(forbidden,text,StringComparison.OrdinalIgnoreCase);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull((await db.MfaRecoveryCodes.SingleAsync(c=>c.UserId==a.Id)).ConsumedAt);
        Assert.Equal(HttpStatusCode.TooManyRequests,(await a.Client.PostAsJsonAsync("/api/account/data/export",Proof(a))).StatusCode);
        using var fsharp=app.CreateClient();fsharp.DefaultRequestHeaders.Authorization=new("Bearer",a.Finance);
        Assert.Equal(HttpStatusCode.OK,(await fsharp.GetAsync("/api/integrations/finance/subscriptions")).StatusCode);
    }

    [Fact]
    public async Task Summary_and_mutations_require_explicit_current_account_binding_and_csrf()
    {
        using var app=App();var a=await Seed(app);var b=await Seed(app);
        var originalContext=a.Client.DefaultRequestHeaders.GetValues(AuthTokenService.AccountContextHeader).Single();
        a.Client.DefaultRequestHeaders.Remove(AuthTokenService.AccountContextHeader);
        Assert.Equal(HttpStatusCode.Conflict,(await a.Client.GetAsync("/api/account/data")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await a.Client.PostAsJsonAsync("/api/account/data/delete",Proof(a))).StatusCode);
        a.Client.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader,b.Client.DefaultRequestHeaders.GetValues(AuthTokenService.AccountContextHeader));
        Assert.Equal(HttpStatusCode.Conflict,(await a.Client.PostAsJsonAsync("/api/account/data/export",Proof(a))).StatusCode);
        a.Client.DefaultRequestHeaders.Remove(AuthTokenService.AccountContextHeader);a.Client.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader,originalContext);
        a.Client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        Assert.Equal(HttpStatusCode.Forbidden,(await a.Client.PostAsJsonAsync("/api/account/data/delete",Proof(a))).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_backoff_is_persistent_and_username_confirmation_cannot_target_another_user()
    {
        using var app=App();var a=await Seed(app);var b=await Seed(app);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.Client.PostAsJsonAsync("/api/account/data/delete",Proof(a,confirmation:b.Username))).StatusCode);
        for(var i=0;i<5;i++)Assert.Equal(HttpStatusCode.BadRequest,(await a.Client.PostAsJsonAsync("/api/account/data/export",Proof(a,"wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.Client.PostAsJsonAsync("/api/account/data/delete",Proof(a))).StatusCode);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True((await db.Users.SingleAsync(u=>u.Id==a.Id)).LoginBlockedUntil>DateTime.UtcNow);
        Assert.True(await db.Users.AnyAsync(u=>u.Id==b.Id));Assert.False(await db.AccountDeletionRecords.AnyAsync(r=>r.UserId==a.Id));
    }

    [Fact]
    public async Task Deletion_requires_mfa_and_ignores_neither_a_missing_nor_reused_code()
    {
        using var app=App();var a=await Seed(app,true);
        Assert.Equal(HttpStatusCode.BadRequest,(await a.Client.PostAsJsonAsync("/api/account/data/delete",new{currentPassword=Password,confirmationUsername=a.Username})).StatusCode);
        (await a.Client.PostAsJsonAsync("/api/account/data/export",Proof(a))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest,(await a.Client.PostAsJsonAsync("/api/account/data/delete",Proof(a))).StatusCode);
        using var scope=app.Services.CreateScope();Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.AnyAsync(u=>u.Id==a.Id));
    }

    [PostgresFact]
    public async Task Deletion_cascades_all_owned_rows_revokes_browser_and_finance_and_preserves_other_accounts()
    {
        using var app=App();var a=await Seed(app,true);var b=await Seed(app);
        var response=await a.Client.PostAsJsonAsync("/api/account/data/delete",Proof(a));response.EnsureSuccessStatusCode();
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),c=>c.StartsWith("access_token=;")||c.StartsWith("access_token=; "));
        Assert.Equal(HttpStatusCode.Unauthorized,(await a.Client.GetAsync("/api/auth/me")).StatusCode);
        using var fsharp=app.CreateClient();fsharp.DefaultRequestHeaders.Authorization=new("Bearer",a.Finance);
        Assert.Equal(HttpStatusCode.Unauthorized,(await fsharp.GetAsync("/api/integrations/finance/subscriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await b.Client.GetAsync("/api/account/data")).StatusCode);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Users.AnyAsync(u=>u.Id==a.Id));Assert.False(await db.Subscriptions.AnyAsync(s=>s.UserId==a.Id));
        Assert.False(await db.SavingsGoals.AnyAsync(g=>g.UserId==a.Id));Assert.False(await db.Budgets.AnyAsync(b=>b.UserId==a.Id));
        Assert.False(await db.RefreshTokens.AnyAsync(t=>t.UserId==a.Id));Assert.False(await db.VerificationCodes.AnyAsync(c=>c.UserId==a.Id));
        Assert.False(await db.MfaRecoveryCodes.AnyAsync(c=>c.UserId==a.Id));Assert.False(await db.MfaLoginChallenges.AnyAsync(c=>c.UserId==a.Id));
        Assert.False(await db.NotificationPreferences.AnyAsync(p=>p.UserId==a.Id));Assert.False(await db.NotificationOutbox.AnyAsync(n=>n.UserId==a.Id));
        Assert.Single(await db.AccountDeletionRecords.Where(r=>r.UserId==a.Id).ToListAsync());
        Assert.False(await db.SubscriptionPriceChanges.AnyAsync(p=>p.SubscriptionId!=Guid.Empty&&!db.Subscriptions.Any(s=>s.Id==p.SubscriptionId)));
        Assert.False(await db.GoalContributions.AnyAsync(c=>!db.SavingsGoals.Any(g=>g.Id==c.GoalId)));
    }

    [PostgresFact]
    public async Task Failure_to_delete_rolls_back_the_receipt_and_preserves_data_and_access()
    {
        using var app=App();var a=await Seed(app);using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"AccountDeletionBlockFixture\" (\"UserId\" uuid REFERENCES \"Users\" (\"Id\") ON DELETE RESTRICT)");
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AccountDeletionBlockFixture\" VALUES ({a.Id})");
            Assert.Equal(HttpStatusCode.InternalServerError,(await a.Client.PostAsJsonAsync("/api/account/data/delete",Proof(a))).StatusCode);
            Assert.True(await db.Users.AnyAsync(u=>u.Id==a.Id));Assert.True(await db.Subscriptions.AnyAsync(s=>s.UserId==a.Id));
            Assert.False(await db.AccountDeletionRecords.AnyAsync(r=>r.UserId==a.Id));
            Assert.Equal(HttpStatusCode.OK,(await a.Client.GetAsync("/api/auth/me")).StatusCode);
        }
        finally{await db.Database.ExecuteSqlRawAsync("DROP TABLE \"AccountDeletionBlockFixture\"");}
    }

    [PostgresFact]
    public async Task Concurrent_deletion_has_one_commit_and_no_orphan_receipts()
    {
        using var app=App();var a=await Seed(app);
        var responses=await Task.WhenAll(a.Client.PostAsJsonAsync("/api/account/data/delete",Proof(a)),a.Client.PostAsJsonAsync("/api/account/data/delete",Proof(a)));
        Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.OK);
        Assert.All(responses,r=>Assert.Contains(r.StatusCode,new[]{HttpStatusCode.OK,HttpStatusCode.Conflict,HttpStatusCode.Unauthorized}));
        using var scope=app.Services.CreateScope();Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().AccountDeletionRecords.Where(r=>r.UserId==a.Id).ToListAsync());
    }

    [Fact]
    public async Task Data_routes_reject_anonymous_requests()
    {
        using var app=App();using var client=app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/account/data")).StatusCode);
        foreach(var route in new[]{"export","delete"})Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/account/data/"+route,new{currentPassword=Password,confirmationUsername="synthetic"})).StatusCode);
    }

    [PostgresFact]
    public async Task Oversized_archive_is_rejected_without_an_attachment_or_partial_download()
    {
        using var app=App();var a=await Seed(app);using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Subscriptions.AddRange(Enumerable.Range(0,1500).Select(i=>new SubscriptionItem{UserId=a.Id,Name="large-fixture-"+i,Notes=new string('ă',2000),NextBillingDate=DateOnly.FromDateTime(DateTime.UtcNow)}));
        await db.SaveChangesAsync();
        var response=await a.Client.PostAsJsonAsync("/api/account/data/export",Proof(a));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,response.StatusCode);Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length<8192);
        var b=await Seed(app);(await b.Client.PostAsJsonAsync("/api/account/data/export",Proof(b))).EnsureSuccessStatusCode();
    }

    private static async Task WaitForLock(string connectionString,string relation)
    {
        await using var observer=new Npgsql.NpgsqlConnection(connectionString);await observer.OpenAsync();
        for(var i=0;i<100;i++)
        {
            await using var query=new Npgsql.NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query ILIKE @pattern)",observer);
            query.Parameters.AddWithValue("pattern","%"+relation+"%");
            if(await query.ExecuteScalarAsync() is true)return;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException("Expected the isolated request to wait for "+relation);
    }

    [PostgresFact]
    public async Task A_session_revoked_while_deletion_waits_for_the_user_lock_cannot_delete_the_account()
    {
        using var app=App();var a=await Seed(app);using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectionString=db.Database.GetConnectionString()!;
        await using var connection=new Npgsql.NpgsqlConnection(connectionString);await connection.OpenAsync();
        await using var tx=await connection.BeginTransactionAsync();
        await using(var held=new Npgsql.NpgsqlCommand("SELECT 1 FROM \"Users\" WHERE \"Id\"=@id FOR UPDATE",connection,tx))
        {held.Parameters.AddWithValue("id",a.Id);await held.ExecuteScalarAsync();}
        var pending=a.Client.PostAsJsonAsync("/api/account/data/delete",Proof(a));
        await WaitForLock(connectionString,"Users");
        await using(var revoke=new Npgsql.NpgsqlCommand("UPDATE \"RefreshTokens\" SET \"RevokedAt\"=now() WHERE \"Id\"=@id",connection,tx))
        {revoke.Parameters.AddWithValue("id",a.SessionId);await revoke.ExecuteNonQueryAsync();}
        await tx.CommitAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,(await pending).StatusCode);
        Assert.True(await db.Users.AnyAsync(u=>u.Id==a.Id));Assert.False(await db.AccountDeletionRecords.AnyAsync(r=>r.UserId==a.Id));
    }

    [PostgresFact]
    public async Task Export_uses_one_snapshot_even_when_business_data_changes_between_sections()
    {
        using var app=App();var a=await Seed(app);using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectionString=db.Database.GetConnectionString()!;
        await using var connection=new Npgsql.NpgsqlConnection(connectionString);await connection.OpenAsync();
        await using var tx=await connection.BeginTransactionAsync();
        await using(var held=new Npgsql.NpgsqlCommand("LOCK TABLE \"Budgets\" IN ACCESS EXCLUSIVE MODE",connection,tx))await held.ExecuteNonQueryAsync();
        var pending=a.Client.PostAsJsonAsync("/api/account/data/export",Proof(a));await WaitForLock(connectionString,"Budgets");
        var subscription=await db.Subscriptions.SingleAsync(s=>s.UserId==a.Id);subscription.Name="after-snapshot";
        var goal=await db.SavingsGoals.SingleAsync(g=>g.UserId==a.Id);
        db.GoalContributions.Add(new GoalContribution{Id=Guid.NewGuid(),GoalId=goal.Id,Amount=5,ContributedOn=DateOnly.FromDateTime(DateTime.UtcNow)});
        await db.SaveChangesAsync();await tx.CommitAsync();
        var response=await pending;response.EnsureSuccessStatusCode();var archive=await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("private-"+a.Id,archive.GetProperty("subscriptions")[0].GetProperty("name").GetString());
        Assert.Single(archive.GetProperty("contributions").EnumerateArray());
        Assert.Equal(2,await db.GoalContributions.CountAsync(c=>c.GoalId==goal.Id));
    }

    [Fact]
    public async Task Archive_buffer_enforces_the_byte_limit_and_releases_capacity_on_error_or_disposal()
    {
        var releases=0;using(var stream=new ArchiveBuffer(()=>releases++))
        {
            var block=new byte[1024*1024];for(var i=0;i<16;i++)await stream.WriteAsync(block);
            await Assert.ThrowsAsync<ArchiveTooLargeException>(async()=>await stream.WriteAsync(new byte[1]));
            Assert.Equal(ArchiveBuffer.MaximumBytes,stream.Length);
        }
        Assert.Equal(1,releases);
        using var first=AccountArchive.TryBegin();using var second=AccountArchive.TryBegin();Assert.NotNull(first);Assert.NotNull(second);Assert.Null(AccountArchive.TryBegin());
        first.Dispose();using var next=AccountArchive.TryBegin();Assert.NotNull(next);
    }
}
