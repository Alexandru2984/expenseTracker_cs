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
public class SavingsGoalTests(TestApiFactory factory)
{
    private static object Goal(Guid? version = null, string currency = "eur", decimal targetAmount = 100, bool isArchived = false) =>
        new { name = "  Travel <b>fund</b>  ", description = "Private manual plan", currency, targetAmount,
            monthlyContribution = 20, targetDate = "2027-12-31", isArchived, version };
    private static async Task<JsonElement> Create(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/goals", Goal());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task<JsonElement> Current(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>("/api/goals"))[0];
    private static object Entry(Guid version, Guid? id = null, decimal amount = 25) => new {
        id = id ?? Guid.NewGuid(), version, amount, contributedOn = DateOnly.FromDateTime(DateTime.UtcNow), note = "  Private contribution  " };

    [Fact]
    public async Task Goals_and_history_are_owner_scoped_and_cascade_delete_only_the_owned_goal()
    {
        var (owner, _) = await TestAccount.Create(factory); var (other, _) = await TestAccount.Create(factory);
        var created = await Create(owner); var id = created.GetProperty("id").GetGuid();
        var version = created.GetProperty("version").GetGuid();
        var response = await owner.PostAsJsonAsync($"/api/goals/{id}/contributions", Entry(version));
        response.EnsureSuccessStatusCode(); var contribution = await response.Content.ReadFromJsonAsync<JsonElement>();
        version = contribution.GetProperty("version").GetGuid(); var entryId = contribution.GetProperty("id").GetGuid();
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/goals")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/goals/{id}/contributions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/goals/{id}", Goal(version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/goals/{id}/contributions", Entry(version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/goals/{id}/contributions/{entryId}?version={version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/goals/{id}?version={version}")).StatusCode);
        var list = await owner.GetAsync("/api/goals"); Assert.Equal("no-store", list.Headers.CacheControl?.ToString());
        var current = await Current(owner);
        Assert.Equal("Travel <b>fund</b>", current.GetProperty("name").GetString());
        Assert.Equal("EUR", current.GetProperty("currency").GetString()); Assert.Equal(25, current.GetProperty("savedAmount").GetDecimal());
        Assert.Equal(75, current.GetProperty("remaining").GetDecimal()); Assert.Equal(25, current.GetProperty("progressPercent").GetDecimal());
        Assert.DoesNotContain("userId", current.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/goals/{id}?version={version}")).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().GoalContributions.AnyAsync(c => c.GoalId == id));
    }

    [Fact]
    public async Task Idempotent_retry_does_not_duplicate_and_reused_key_with_changed_payload_is_rejected()
    {
        var (client, _) = await TestAccount.Create(factory); var saved = await Create(client);
        var id = saved.GetProperty("id").GetGuid(); var version = saved.GetProperty("version").GetGuid(); var key = Guid.NewGuid();
        var first = await client.PostAsJsonAsync($"/api/goals/{id}/contributions", Entry(version, key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/goals/{id}/contributions", Entry(version, key))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/goals/{id}/contributions", Entry(version, key, 30))).StatusCode);
        var current = await Current(client);
        Assert.Equal(25, current.GetProperty("savedAmount").GetDecimal()); Assert.Equal(1, current.GetProperty("contributionCount").GetInt32());
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/goals/{id}/contributions?take=1");
        Assert.Equal("Private contribution", history.GetProperty("items")[0].GetProperty("note").GetString());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>($"/api/goals/{id}/contributions?skip=1")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/goals/{id}/contributions?take=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/goals/{id}/contributions?skip=-1")).StatusCode);
    }

    [Fact]
    public async Task Stale_writes_and_deletes_fail_and_currency_is_locked_until_contributions_are_removed()
    {
        var (client, _) = await TestAccount.Create(factory); var saved = await Create(client);
        var id = saved.GetProperty("id").GetGuid(); var version = saved.GetProperty("version").GetGuid(); var key = Guid.NewGuid();
        var contribution = await client.PostAsJsonAsync($"/api/goals/{id}/contributions", Entry(version, key, 150)); contribution.EnsureSuccessStatusCode();
        var current = await Current(client); var fresh = current.GetProperty("version").GetGuid();
        Assert.Equal("completed", current.GetProperty("status").GetString()); Assert.Equal(100, current.GetProperty("progressPercent").GetDecimal());
        Assert.Equal(0, current.GetProperty("remaining").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/goals/{id}", Goal(version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/goals/{id}/contributions", Entry(version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/goals/{id}?version={version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/goals/{id}", Goal(fresh, "USD"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/goals/{id}/contributions/{key}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/goals/{id}/contributions/{key}?version={version}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/goals/{id}/contributions/{key}?version={fresh}")).StatusCode);
        current = await Current(client); Assert.Equal(0, current.GetProperty("savedAmount").GetDecimal());
        (await client.PutAsJsonAsync($"/api/goals/{id}", Goal(current.GetProperty("version").GetGuid(), "USD", isArchived: true))).EnsureSuccessStatusCode();
        current = await Current(client); Assert.Equal("archived", current.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/goals/{id}/contributions", Entry(current.GetProperty("version").GetGuid()))).StatusCode);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1000001)] [InlineData(1.234)]
    public async Task Invalid_amounts_are_rejected_without_writes(decimal amount)
    {
        var (client, _) = await TestAccount.Create(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/goals", Goal(targetAmount: amount))).StatusCode);
        var goal = await Create(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/goals/{goal.GetProperty("id")}/contributions", Entry(goal.GetProperty("version").GetGuid(), amount: amount))).StatusCode);
        Assert.Equal(0, (await Current(client)).GetProperty("savedAmount").GetDecimal());
    }

    [Fact]
    public async Task Dates_missing_keys_invalid_currency_and_cross_tab_context_are_rejected()
    {
        var (client, _) = await TestAccount.Create(factory); var goal = await Create(client);
        var path = $"/api/goals/{goal.GetProperty("id")}/contributions";
        var version = goal.GetProperty("version").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { version, amount=1, contributedOn="2099-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { id=Guid.NewGuid(), version, amount=1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/goals", Goal(currency:"<b>"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/goals", new { name="   ",targetAmount=1,currency="EUR" })).StatusCode);
        var (other, _) = await TestAccount.Create(factory);
        client.DefaultRequestHeaders.Add(AuthTokenService.AccountContextHeader,
            (await other.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("accountContext").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/goals")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, Entry(version))).StatusCode);
        client.DefaultRequestHeaders.Remove(AuthTokenService.AccountContextHeader);
        Assert.Equal(0, (await Current(client)).GetProperty("savedAmount").GetDecimal());
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/goals")).StatusCode);
    }

    [PostgresFact]
    public async Task Concurrent_contributions_have_one_winner_and_identical_retries_return_the_existing_entry()
    {
        var (client, _) = await TestAccount.Create(factory); var goal = await Create(client);
        var path = $"/api/goals/{goal.GetProperty("id")}/contributions"; var version = goal.GetProperty("version").GetGuid();
        var results = await Task.WhenAll(client.PostAsJsonAsync(path, Entry(version)), client.PostAsJsonAsync(path, Entry(version)));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var current = await Current(client); var entry = Entry(current.GetProperty("version").GetGuid());
        results = await Task.WhenAll(client.PostAsJsonAsync(path, entry), client.PostAsJsonAsync(path, entry));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(50, (await Current(client)).GetProperty("savedAmount").GetDecimal());
    }

    [PostgresFact]
    public async Task Quotas_and_total_caps_are_enforced_inside_the_account_lock()
    {
        var (client, userId) = await TestAccount.Create(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SavingsGoals.AddRange(Enumerable.Range(0, 49).Select(i => new SavingsGoal { UserId=userId, Name="Goal "+i, TargetAmount=1 }));
            await db.SaveChangesAsync();
        }
        var attempts = await Task.WhenAll(client.PostAsJsonAsync("/api/goals", Goal()), client.PostAsJsonAsync("/api/goals", Goal()));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Conflict);
        var goal = await Current(client); var path = $"/api/goals/{goal.GetProperty("id")}/contributions";
        (await client.PostAsJsonAsync(path, Entry(goal.GetProperty("version").GetGuid(), amount: 1000000))).EnsureSuccessStatusCode();
        goal = await Current(client);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, Entry(goal.GetProperty("version").GetGuid()))).StatusCode);
    }

    [PostgresFact]
    public async Task Contribution_quota_is_per_account_and_cannot_be_bypassed_with_another_goal()
    {
        var (client, owner) = await TestAccount.Create(factory); var (_, other) = await TestAccount.Create(factory);
        var goal = await Create(client); var id = goal.GetProperty("id").GetGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GoalContributions.AddRange(Enumerable.Range(0, 5000).Select(_ => new GoalContribution
                { Id=Guid.NewGuid(), GoalId=id, Amount=.01m, ContributedOn=new(2026,1,1) }));
            db.SavingsGoals.Add(new SavingsGoal { UserId=other, Name="Foreign", TargetAmount=1 });
            await db.SaveChangesAsync();
        }
        goal = await Create(client); id = goal.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/goals/{id}/contributions", Entry(goal.GetProperty("version").GetGuid()))).StatusCode);
        Assert.Equal(5000, (await client.GetFromJsonAsync<JsonElement>("/api/goals")).EnumerateArray().Sum(g => g.GetProperty("contributionCount").GetInt32()));
    }

    [Fact]
    public async Task A_future_contribution_with_valid_keys_is_rejected_and_keys_are_required()
    {
        var (client, _) = await TestAccount.Create(factory); var goal = await Create(client);
        var path = $"/api/goals/{goal.GetProperty("id")}/contributions"; var version = goal.GetProperty("version").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new {
            id=Guid.NewGuid(), version, amount=1, contributedOn=DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new {
            id=Guid.Empty, version, amount=1, contributedOn=DateOnly.FromDateTime(DateTime.UtcNow) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/goals/{goal.GetProperty("id")}", Goal())).StatusCode);
        Assert.Equal(0, (await Current(client)).GetProperty("savedAmount").GetDecimal());
    }
}

public class GoalProgressTests
{
    [Fact]
    public void Calendar_projection_handles_month_ends_leap_year_rounding_and_expired_deadlines()
    {
        var goal = new SavingsGoal { TargetAmount=100, MonthlyContribution=34, TargetDate=new(2028,4,30) };
        var result = GoalProgress.Calculate(goal, 0, 0, new(2028,1,31));
        Assert.Equal(3, result.MonthsToGoal); Assert.Equal(new DateOnly(2028,4,30), result.EstimatedCompletionDate);
        Assert.Equal(33.34m, result.RequiredMonthlyContribution); Assert.Equal("on-track", result.Status);
        goal.TargetDate = new(2028,2,29);
        Assert.Equal(100, GoalProgress.Calculate(goal,0,0,new(2028,1,31)).RequiredMonthlyContribution);
        Assert.Equal("behind", GoalProgress.Calculate(goal,0,0,new(2028,1,31)).Status);
        Assert.Equal("overdue", GoalProgress.Calculate(goal,0,0,new(2028,3,1)).Status);
        Assert.Equal("completed", GoalProgress.Calculate(goal,120,2,new(2028,3,1)).Status);
    }

    [Fact]
    public void Missing_or_tiny_monthly_plans_do_not_overflow_calendar_and_completed_goals_need_no_more_money()
    {
        var goal = new SavingsGoal { TargetAmount=1000000, MonthlyContribution=.01m };
        var result = GoalProgress.Calculate(goal,0,0,new(2026,1,1));
        Assert.Equal(100000000,result.MonthsToGoal); Assert.Null(result.EstimatedCompletionDate);
        goal.MonthlyContribution=0;
        result=GoalProgress.Calculate(goal,0,0,new(2026,1,1)); Assert.Null(result.MonthsToGoal); Assert.Equal("no-plan",result.Status);
        result=GoalProgress.Calculate(goal,1000000,1,new(2026,1,1)); Assert.Equal(0,result.Remaining); Assert.Equal(0,result.MonthsToGoal);
        goal.IsArchived=true; Assert.Equal("archived",GoalProgress.Calculate(goal,1000000,1,new(2026,1,1)).Status);
    }
}
