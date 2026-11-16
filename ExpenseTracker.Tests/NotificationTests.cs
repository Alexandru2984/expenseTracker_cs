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
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Xunit;

namespace ExpenseTracker.Tests;

internal sealed class ReminderClock(DateTime now) : TimeProvider
{
    public DateTime Now { get; set; } = DateTime.SpecifyKind(now, DateTimeKind.Utc);
    public override DateTimeOffset GetUtcNow() => new(Now);
}
internal sealed class RecordingReminderSender : IReminderEmailSender
{
    public bool IsConfigured { get; set; } = true;
    public List<MimeMessage> Messages { get; } = [];
    public Queue<ReminderSendResult> Results { get; } = new();
    public Func<Task>? DuringSend { get; set; }
    public async Task<ReminderSendResult> Send(MimeMessage message, CancellationToken ct)
    {
        lock (Messages) Messages.Add(message);
        if (DuringSend is not null) await DuringSend();
        return Results.Count > 0 ? Results.Dequeue() : ReminderSendResult.Accepted;
    }
}

[Collection("Api")]
public class NotificationTests(TestApiFactory factory)
{
    private static readonly DateTime FixedNow = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
    private WebApplicationFactory<Program> Api(bool available = true) => factory.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services => services.AddSingleton<IReminderEmailSender>(new RecordingReminderSender { IsConfigured = available })));
    private static async Task<(HttpClient client, Guid id)> Account(WebApplicationFactory<Program> app, bool verified = true)
    {
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { Username = "notification_" + Guid.NewGuid().ToString("N"),
            Email = Guid.NewGuid().ToString("N") + "@example.invalid", EmailVerified = verified, PasswordHash = "synthetic-hash" };
        var session = new RefreshToken { UserId = user.Id, TokenHash = AuthTokenService.Hash(Guid.NewGuid().ToString()), ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.Users.Add(user); db.RefreshTokens.Add(session); await db.SaveChangesAsync();
        var jwt = scope.ServiceProvider.GetRequiredService<AuthTokenService>().CreateAccessToken(user, session.Id).token;
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"access_token={jwt}; csrf_token=notification-test-csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", "notification-test-csrf");
        client.DefaultRequestHeaders.Add("X-Notification-Recipient", scope.ServiceProvider.GetRequiredService<NotificationContent>().RecipientHash(user.Id,user.Email));
        return (client, user.Id);
    }
    private static object Settings(Guid version, bool enabled = true, string timeZone = "Europe/Bucharest", int sendHour = 9,
        int leadDays = 7, bool renewals = true, bool trials = true, bool goalDeadlines = true, bool includeNames = false) =>
        new { version, enabled, timeZone, sendHour, leadDays, renewals, trials, goalDeadlines, includeNames };
    private NotificationEngine Engine(IServiceScope scope, RecordingReminderSender sender, ReminderClock clock) => new(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        new NotificationContent(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), sender),
        scope.ServiceProvider.GetRequiredService<VerificationService>(), sender,
        scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), clock, NullLogger<NotificationEngine>.Instance);

    private static async Task<(Guid userId, Guid prefVersion)> Seed(AppDbContext db, bool withItems = true, string zone = "UTC")
    {
        var user = new User { Username = "digest_" + Guid.NewGuid().ToString("N"), Email = Guid.NewGuid().ToString("N") + "@example.invalid",
            EmailVerified = true, PasswordHash = "synthetic-hash" };
        var pref = new NotificationPreference { UserId = user.Id, Enabled = true, TimeZone = zone, SendHour = 9, GoalDeadlines = true,
            ConsentRecordedAt = FixedNow.AddDays(-2), NextDigestAtUtc = FixedNow.AddHours(-1) };
        db.Users.Add(user); db.NotificationPreferences.Add(pref);
        if (withItems)
        {
            db.Subscriptions.Add(new SubscriptionItem { UserId = user.Id, Name = "<img src=x onerror=private()>", Cost = 37, Currency = "EUR",
                NextBillingDate = new(2026, 11, 18), TrialEndsOn = new(2026, 11, 17), Notes = "NEVER_SEND_NOTES", ProviderUrl = "https://private.example.invalid/" });
            db.SavingsGoals.Add(new SavingsGoal { UserId = user.Id, Name = "PRIVATE_GOAL_NAME", Description = "NEVER_SEND_DESCRIPTION",
                TargetAmount = 100, TargetDate = new(2026, 11, 19) });
        }
        await db.SaveChangesAsync(); return (user.Id, pref.Version);
    }

    [Fact]
    public async Task Defaults_are_opt_out_and_get_does_not_create_preferences()
    {
        using var app = Api(); var (client, id) = await Account(app);
        var response = await client.GetAsync("/api/account/notifications"); response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(data.GetProperty("enabled").GetBoolean()); Assert.False(data.GetProperty("includeNames").GetBoolean());
        Assert.Equal(Guid.Empty, data.GetProperty("version").GetGuid()); Assert.True(data.GetProperty("canEnable").GetBoolean());
        using var scope = app.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().NotificationPreferences.AnyAsync(p => p.UserId == id));
        Assert.Contains("Europe/Bucharest", await client.GetFromJsonAsync<string[]>("/api/account/notifications/time-zones") ?? []);
    }

    [Fact]
    public async Task Save_requires_consent_verified_email_and_available_sender_and_stale_versions_fail()
    {
        using var app = Api(); var (client, id) = await Account(app);
        var response = await client.PutAsJsonAsync("/api/account/notifications", Settings(Guid.Empty));
        response.EnsureSuccessStatusCode(); var pref = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(pref.GetProperty("enabled").GetBoolean()); Assert.NotEqual(JsonValueKind.Null, pref.GetProperty("consentRecordedAt").ValueKind);
        Assert.True(pref.GetProperty("nextDigestAtUtc").GetDateTime() > DateTime.UtcNow);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/account/notifications", Settings(Guid.Empty, false))).StatusCode);
        (await client.PutAsJsonAsync("/api/account/notifications", Settings(pref.GetProperty("version").GetGuid(), false))).EnsureSuccessStatusCode();
        pref = await client.GetFromJsonAsync<JsonElement>("/api/account/notifications"); Assert.False(pref.GetProperty("enabled").GetBoolean());
        var (unverified, _) = await Account(app, false);
        Assert.Equal(HttpStatusCode.BadRequest, (await unverified.PutAsJsonAsync("/api/account/notifications", Settings(Guid.Empty))).StatusCode);
        using var unavailable = Api(false); var (blocked, _) = await Account(unavailable);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await blocked.PutAsJsonAsync("/api/account/notifications", Settings(Guid.Empty))).StatusCode);
    }

    [Theory]
    [InlineData("../etc/passwd", 9, 7)] [InlineData("Not/AZone", 9, 7)] [InlineData("UTC", -1, 7)]
    [InlineData("UTC", 24, 7)] [InlineData("UTC", 9, -1)] [InlineData("UTC", 9, 15)]
    public async Task Invalid_preferences_are_rejected(string zone, int hour, int days)
    {
        using var app = Api(); var (client, _) = await Account(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/account/notifications", Settings(Guid.Empty, timeZone: zone, sendHour: hour, leadDays: days))).StatusCode);
    }

    [Fact]
    public async Task Csrf_account_context_and_finance_scope_protect_preferences_and_history()
    {
        using var app = Api(); var (client, id) = await Account(app); var (other, otherId) = await Account(app);
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/account/notifications", Settings(Guid.Empty))).StatusCode);
        client.DefaultRequestHeaders.Add("X-CSRF-Token", "notification-test-csrf");
        client.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader,
            (await other.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("accountContext").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/account/notifications/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/account/notifications", Settings(Guid.Empty))).StatusCode);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var token = FinanceTokens.Create(); db.RefreshTokens.Add(new RefreshToken { UserId = id, TokenHash = FinanceTokens.Hash(token), ExpiresAt = DateTime.UtcNow.AddDays(1) });
        db.NotificationOutbox.Add(new NotificationOutbox { UserId = otherId, LocalDate = DateOnly.FromDateTime(DateTime.UtcNow), State = NotificationState.Sent });
        await db.SaveChangesAsync(); client.DefaultRequestHeaders.Remove(AuthTokenService.AccountContextHeader);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/account/notifications/history")).EnumerateArray());
        using var finance = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        finance.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await finance.GetAsync("/api/account/notifications")).StatusCode);
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/account/notifications/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/account/notifications",
            Settings(Guid.Empty, renewals: false, trials: false, goalDeadlines: false))).StatusCode);
    }

    [PostgresFact]
    public async Task Concurrent_first_opt_ins_have_one_winner()
    {
        using var app = Api(); var (client, _) = await Account(app);
        var responses = await Task.WhenAll(client.PutAsJsonAsync("/api/account/notifications", Settings(Guid.Empty)),
            client.PutAsJsonAsync("/api/account/notifications", Settings(Guid.Empty)));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_form_opened_for_the_previous_inbox_cannot_enable_the_new_verified_inbox()
    {
        using var app=Api();var (client,id)=await Account(app);
        using(var scope=app.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user=await db.Users.SingleAsync(u=>u.Id==id);user.Email=Guid.NewGuid().ToString("N")+"@example.invalid";
            await db.SaveChangesAsync();
            var session=await db.RefreshTokens.SingleAsync(s=>s.UserId==id);
            var jwt=scope.ServiceProvider.GetRequiredService<AuthTokenService>().CreateAccessToken(user,session.Id).token;
            client.DefaultRequestHeaders.Remove("Cookie");client.DefaultRequestHeaders.Add("Cookie",$"access_token={jwt}; csrf_token=notification-test-csrf");
        }
        Assert.Equal(HttpStatusCode.Conflict,(await client.PutAsJsonAsync("/api/account/notifications",Settings(Guid.Empty))).StatusCode);
        using var check=app.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<AppDbContext>().NotificationPreferences.AnyAsync(p=>p.UserId==id));
    }

    [PostgresFact]
    public async Task Opt_out_cancels_pending_and_preparing_work_and_preserves_consent_audit()
    {
        using var app = Api(); var (client, id) = await Account(app);
        var response = await client.PutAsJsonAsync("/api/account/notifications", Settings(Guid.Empty)); response.EnsureSuccessStatusCode();
        var pref = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.NotificationOutbox.AddRange(new NotificationOutbox { UserId=id, LocalDate=new(2026,11,14) },
            new NotificationOutbox { UserId=id, LocalDate=new(2026,11,15), State=NotificationState.Preparing, Attempts=1,
                LeaseId=Guid.NewGuid(), LeaseExpiresAtUtc=DateTime.UtcNow.AddMinutes(2) });
        await db.SaveChangesAsync();
        response = await client.PutAsJsonAsync("/api/account/notifications",Settings(pref.GetProperty("version").GetGuid(),false));
        response.EnsureSuccessStatusCode(); var disabled = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(Math.Abs((pref.GetProperty("consentRecordedAt").GetDateTime()
            - disabled.GetProperty("consentRecordedAt").GetDateTime()).Ticks), 0L, 9L); // PostgreSQL microsecond precision.
        Assert.Null(disabled.GetProperty("nextDigestAtUtc").GetString());
        Assert.All(await db.NotificationOutbox.AsNoTracking().Where(q=>q.UserId==id).ToListAsync(),
            row=>Assert.Equal(NotificationState.Canceled,row.State));
    }

    [PostgresFact]
    public async Task Two_schedulers_and_dispatchers_do_not_duplicate_a_digest()
    {
        using var first = factory.Services.CreateScope(); using var second = factory.Services.CreateScope();
        var db = first.ServiceProvider.GetRequiredService<AppDbContext>(); var (id, _) = await Seed(db);
        var sender = new RecordingReminderSender(); var clock = new ReminderClock(FixedNow);
        var a = Engine(first,sender,clock); var b = Engine(second,sender,clock);
        await Task.WhenAll(a.ScheduleDue(),b.ScheduleDue());
        Assert.Single(await db.NotificationOutbox.AsNoTracking().Where(q=>q.UserId==id).ToListAsync());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.DuringSend=()=>{ entered.TrySetResult(); return release.Task; };
        var sending=a.DispatchOne(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { Assert.False(await b.DispatchOne()); }
        finally { release.TrySetResult(); }
        Assert.True(await sending); Assert.Single(sender.Messages);
        Assert.Equal(NotificationState.Sent,(await db.NotificationOutbox.AsNoTracking().SingleAsync(q=>q.UserId==id)).State);
    }

    [PostgresFact]
    public async Task A_worker_ack_after_lease_recovery_cannot_overwrite_uncertain_status()
    {
        using var first = factory.Services.CreateScope(); using var second = factory.Services.CreateScope();
        var db = first.ServiceProvider.GetRequiredService<AppDbContext>(); var (id, _) = await Seed(db);
        var sender = new RecordingReminderSender(); var clock = new ReminderClock(FixedNow);
        var engine=Engine(first,sender,clock); await engine.ScheduleDue();
        sender.DuringSend=async()=>{clock.Now=FixedNow.AddMinutes(3);await Engine(second,sender,clock).RecoverAndClean();};
        await engine.DispatchOne();
        Assert.Equal(NotificationState.Uncertain,(await db.NotificationOutbox.AsNoTracking().SingleAsync(q=>q.UserId==id)).State);
        Assert.Single(sender.Messages); Assert.False(await engine.DispatchOne());
    }

    [PostgresFact]
    public async Task An_ack_timeout_does_not_terminate_the_background_worker()
    {
        using var seed=factory.Services.CreateScope();var db=seed.ServiceProvider.GetRequiredService<AppDbContext>();
        await Seed(db);
        var services=new ServiceCollection();services.AddLogging();
        services.AddSingleton(seed.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());
        services.AddSingleton(seed.ServiceProvider.GetRequiredService<AuthTokenService>());
        var options=seed.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
        services.AddScoped(_=>new AppDbContext(options));services.AddScoped<EmailService>();services.AddScoped<VerificationService>();
        services.AddScoped<NotificationContent>();services.AddScoped<NotificationEngine>();
        services.AddSingleton<TimeProvider>(new ReminderClock(FixedNow));
        var sender=new RecordingReminderSender();services.AddSingleton<IReminderEmailSender>(sender);
        using var provider=services.BuildServiceProvider();
        await using var connection=new Npgsql.NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync();await using var transaction=await connection.BeginTransactionAsync();
        var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.DuringSend=async()=>{
            await using var command=new Npgsql.NpgsqlCommand("LOCK TABLE \"NotificationOutbox\" IN ACCESS EXCLUSIVE MODE",connection,transaction);
            await command.ExecuteNonQueryAsync();blocked.TrySetResult();
        };
        using var worker=new NotificationWorker(provider.GetRequiredService<IServiceScopeFactory>(),NullLogger<NotificationWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(TimeSpan.FromSeconds(7)); // The ACK has its own five-second deadline.
            Assert.False(worker.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await transaction.RollbackAsync();
            await worker.StopAsync(default);
        }
        // Sending remains eligible for conservative lease recovery after the ACK timeout.
    }

    [PostgresFact]
    public async Task Preview_excludes_foreign_paused_past_and_completed_items_and_does_not_move_money()
    {
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (id, _)=await Seed(db);var (other, _)=await Seed(db,false);
        db.Subscriptions.AddRange(new SubscriptionItem {UserId=other,Name="FOREIGN_NAME",NextBillingDate=new(2026,11,17)},
            new SubscriptionItem {UserId=id,Name="PAUSED_NAME",IsActive=false,NextBillingDate=new(2026,11,17)},
            new SubscriptionItem {UserId=id,Name="PAST_NAME",NextBillingDate=new(2026,11,15),TrialEndsOn=new(2026,11,15)});
        var completed=new SavingsGoal {UserId=id,Name="COMPLETED_GOAL",TargetAmount=1,TargetDate=new(2026,11,19)};
        db.SavingsGoals.Add(completed);db.GoalContributions.Add(new GoalContribution {GoalId=completed.Id,Id=Guid.NewGuid(),Amount=1,ContributedOn=new(2026,11,16)});
        await db.SaveChangesAsync();
        var content=new NotificationContent(db,scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),new RecordingReminderSender());
        var pref=await db.NotificationPreferences.AsNoTracking().SingleAsync(p=>p.UserId==id);
        var preview=await content.Preview(id,pref,new(2026,11,16),default);
        Assert.Equal(1,preview.RenewalCount);Assert.Equal(1,preview.TrialCount);Assert.Equal(1,preview.GoalCount);Assert.Equal(3,preview.Items.Count);
        Assert.DoesNotContain(preview.Items,i=>i.Name is "FOREIGN_NAME" or "PAUSED_NAME" or "PAST_NAME" or "COMPLETED_GOAL");
        pref.LeadDays=0; Assert.False((await content.Preview(id,pref,new(2026,11,16),default)).HasItems);
        // This test must not leave a due preference for a later worker test.
        foreach (var own in await db.NotificationPreferences.Where(p=>p.UserId==id || p.UserId==other).ToListAsync()) own.Enabled=false;
        await db.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task Scheduler_is_daily_and_sender_omits_names_notes_costs_urls_and_goal_descriptions_by_default()
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (id, _) = await Seed(db); var sender = new RecordingReminderSender(); var clock = new ReminderClock(FixedNow);
        var engine = Engine(scope, sender, clock);
        await engine.ScheduleDue(); await engine.ScheduleDue();
        Assert.Single(await db.NotificationOutbox.AsNoTracking().Where(q => q.UserId == id).ToListAsync());
        // Other tests' accounts are either disabled or scheduled in the future.
        while (await engine.DispatchOne()) { }
        var message = Assert.Single(sender.Messages, m => m.TextBody?.Contains("• 1 scadențe") == true);
        Assert.DoesNotContain("PRIVATE_GOAL_NAME", message.TextBody); Assert.DoesNotContain("<img", message.HtmlBody);
        Assert.DoesNotContain("NEVER_SEND", message.TextBody); Assert.DoesNotContain("private.example", message.TextBody);
        Assert.DoesNotContain("37", message.TextBody); Assert.Contains(ReminderMessage.SettingsUrl, message.TextBody);
        var row = await db.NotificationOutbox.AsNoTracking().SingleAsync(q => q.UserId == id);
        Assert.Equal(NotificationState.Sent, row.State); Assert.Equal(1, row.Attempts);
        Assert.Single(message.To); Assert.Equal("reminder-" + row.Id.ToString("N") + "@expenses.micutu.com", message.MessageId);
        Assert.DoesNotContain("@", row.RecipientHash); Assert.Null(row.LeaseId);
    }

    [PostgresFact]
    public async Task Safe_failures_back_off_at_five_and_thirty_minutes_and_stop_after_three_attempts()
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (id, _) = await Seed(db); var sender = new RecordingReminderSender(); var clock = new ReminderClock(FixedNow);
        sender.Results.Enqueue(ReminderSendResult.Retryable); sender.Results.Enqueue(ReminderSendResult.Retryable); sender.Results.Enqueue(ReminderSendResult.Retryable);
        var engine = Engine(scope, sender, clock); await engine.ScheduleDue(); await engine.DispatchOne();
        var row = await db.NotificationOutbox.AsNoTracking().SingleAsync(q => q.UserId == id);
        Assert.Equal(NotificationState.Pending, row.State); Assert.Equal(FixedNow.AddMinutes(5), row.NextAttemptAtUtc);
        Assert.False(await engine.DispatchOne()); clock.Now = FixedNow.AddMinutes(5); await engine.DispatchOne();
        row = await db.NotificationOutbox.AsNoTracking().SingleAsync(q => q.UserId == id);
        Assert.Equal(FixedNow.AddMinutes(35), row.NextAttemptAtUtc); clock.Now = FixedNow.AddMinutes(35); await engine.DispatchOne();
        row = await db.NotificationOutbox.AsNoTracking().SingleAsync(q => q.UserId == id);
        Assert.Equal(NotificationState.Failed, row.State); Assert.Equal(3, row.Attempts);
        Assert.Single(sender.Messages.Select(m => m.MessageId).Distinct()); Assert.False(await engine.DispatchOne());
    }

    [PostgresFact]
    public async Task Ambiguous_acceptance_never_auto_retries_and_crashed_send_is_uncertain()
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (id, _) = await Seed(db); var sender = new RecordingReminderSender(); sender.Results.Enqueue(ReminderSendResult.Uncertain);
        var clock = new ReminderClock(FixedNow); var engine = Engine(scope, sender, clock);
        await engine.ScheduleDue(); await engine.DispatchOne();
        Assert.Equal(NotificationState.Uncertain, (await db.NotificationOutbox.AsNoTracking().SingleAsync(q => q.UserId == id)).State);
        clock.Now = FixedNow.AddHours(2); Assert.False(await engine.DispatchOne());
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        db.NotificationOutbox.Add(new NotificationOutbox { UserId = id, LocalDate = new(2026,11,15), State = NotificationState.Sending,
            Attempts = 1, LeaseId = Guid.NewGuid(), LeaseExpiresAtUtc = FixedNow.AddMinutes(-1) });
        await db.SaveChangesAsync(); await engine.RecoverAndClean();
        Assert.All(await db.NotificationOutbox.AsNoTracking().Where(q => q.UserId == id).ToListAsync(), r => Assert.Equal(NotificationState.Uncertain, r.State));
    }

    [PostgresFact]
    public async Task Stale_preparation_recovers_but_a_new_local_day_cancels_old_mail()
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (id, _) = await Seed(db); var sender = new RecordingReminderSender(); var clock = new ReminderClock(FixedNow);
        var engine = Engine(scope, sender, clock); await engine.ScheduleDue();
        var row = await db.NotificationOutbox.SingleAsync(q => q.UserId == id);
        row.State = NotificationState.Preparing; row.Attempts = 1; row.LeaseId = Guid.NewGuid(); row.LeaseExpiresAtUtc = FixedNow.AddMinutes(-1);
        await db.SaveChangesAsync(); await engine.RecoverAndClean();
        row = await db.NotificationOutbox.AsNoTracking().SingleAsync(q => q.UserId == id); Assert.Equal(NotificationState.Pending, row.State);
        clock.Now = FixedNow.AddDays(1); await engine.DispatchOne();
        row = await db.NotificationOutbox.AsNoTracking().SingleAsync(q => q.UserId == id);
        Assert.Equal(NotificationState.Canceled, row.State); Assert.Empty(sender.Messages);
    }

    [PostgresFact]
    public async Task Email_changes_opt_out_and_cancel_queued_work_in_the_same_transaction()
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (id, _) = await Seed(db); var sender = new RecordingReminderSender(); var engine = Engine(scope, sender, new(FixedNow));
        await engine.ScheduleDue(); db.ChangeTracker.Clear();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var user = await db.Users.SingleAsync(u => u.Id == id);
            await scope.ServiceProvider.GetRequiredService<VerificationService>().LockAccountAsync(user);
            await NotificationContent.DisableForEmailChange(db, id); user.Email = "changed_" + Guid.NewGuid().ToString("N") + "@example.invalid";
            await db.SaveChangesAsync(); await tx.CommitAsync();
        }
        while (await engine.DispatchOne()) { }
        Assert.Empty(sender.Messages);
        Assert.False((await db.NotificationPreferences.AsNoTracking().SingleAsync(p => p.UserId == id)).Enabled);
        Assert.Equal(NotificationState.Canceled, (await db.NotificationOutbox.AsNoTracking().SingleAsync(q => q.UserId == id)).State);
    }

    [PostgresFact]
    public async Task Changed_recipient_or_preferences_are_rechecked_and_empty_digests_are_not_enqueued()
    {
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (empty, _) = await Seed(db, false); var (id, _) = await Seed(db);
        var sender = new RecordingReminderSender(); var engine = Engine(scope, sender, new(FixedNow));
        await engine.ScheduleDue(); Assert.False(await db.NotificationOutbox.AnyAsync(q => q.UserId == empty));
        var user = await db.Users.SingleAsync(u => u.Id == id); user.Email = Guid.NewGuid().ToString("N") + "@example.invalid";
        await db.SaveChangesAsync(); while (await engine.DispatchOne()) { }
        Assert.Empty(sender.Messages); Assert.Equal(NotificationState.Canceled, (await db.NotificationOutbox.AsNoTracking().SingleAsync(q => q.UserId == id)).State);
    }
}

public class NotificationScheduleTests
{
    [Fact]
    public void Dst_gap_moves_forward_and_overlap_chooses_first_occurrence_once()
    {
        var zone = NotificationSchedule.Zone("Europe/Bucharest")!;
        Assert.NotNull(zone);
        Assert.Equal(new DateTime(2026,3,29,1,0,0,DateTimeKind.Utc), NotificationSchedule.AtLocalHour(new(2026,3,29),3,zone));
        Assert.Equal(new DateTime(2026,10,25,0,0,0,DateTimeKind.Utc), NotificationSchedule.AtLocalHour(new(2026,10,25),3,zone));
        Assert.Equal(new DateTime(2026,10,26,1,0,0,DateTimeKind.Utc),
            NotificationSchedule.Next(new(2026,10,25,0,0,0,DateTimeKind.Utc),zone,3));
    }
    [Fact]
    public void Next_is_in_the_chosen_zone_and_bad_ids_are_rejected()
    {
        Assert.Null(NotificationSchedule.Zone("../../etc/passwd"));
        var zone = NotificationSchedule.Zone("America/New_York")!;
        Assert.Equal(new DateTime(2026,11,16,14,0,0,DateTimeKind.Utc),
            NotificationSchedule.Next(new(2026,11,16,10,0,0,DateTimeKind.Utc),zone,9));
    }
    [Fact]
    public void Name_opt_in_encodes_html_and_never_adds_financial_details()
    {
        var preview = new ExpenseTracker.Api.Dtos.NotificationPreviewDto(new(2026,11,16),7,1,0,0,
            [new("renewal","<img src=x onerror=evil()>",new(2026,11,17))]);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var message = ReminderMessage.Build(config,new NotificationOutbox(),"recipient@example.invalid",preview,true);
        Assert.Contains("&lt;img",message.HtmlBody); Assert.DoesNotContain("<img",message.HtmlBody);
        Assert.DoesNotContain("<img",ReminderMessage.Text(preview,false));
        Assert.Contains("<img",ReminderMessage.Text(preview,true));
    }
}
