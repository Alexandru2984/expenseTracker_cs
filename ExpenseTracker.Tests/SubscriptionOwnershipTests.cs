using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Api.Controllers;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Models;
using Xunit;

namespace ExpenseTracker.Tests;

public class SubscriptionOwnershipTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SubscriptionsController ControllerAs(AppDbContext db, Guid userId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"));

        return new SubscriptionsController(db, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };
    }

    [Fact]
    public async Task User_cannot_read_another_users_subscription()
    {
        var db = NewDb();
        var ownerId = Guid.NewGuid();
        var intruderId = Guid.NewGuid();

        var sub = new SubscriptionItem { Id = Guid.NewGuid(), Name = "Netflix", Currency = "RON", UserId = ownerId };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();

        var intruderResult = await ControllerAs(db, intruderId).GetById(sub.Id);
        Assert.IsType<NotFoundResult>(intruderResult);

        var ownerResult = await ControllerAs(db, ownerId).GetById(sub.Id);
        Assert.IsType<OkObjectResult>(ownerResult);
    }

    [Fact]
    public async Task User_cannot_delete_another_users_subscription()
    {
        var db = NewDb();
        var ownerId = Guid.NewGuid();
        var sub = new SubscriptionItem { Id = Guid.NewGuid(), Name = "Spotify", Currency = "RON", UserId = ownerId };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();

        var result = await ControllerAs(db, Guid.NewGuid()).Delete(sub.Id);
        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(1, await db.Subscriptions.CountAsync()); // untouched
    }

    [Fact]
    public async Task List_only_returns_callers_subscriptions()
    {
        var db = NewDb();
        var meId = Guid.NewGuid();
        db.Subscriptions.Add(new SubscriptionItem { Id = Guid.NewGuid(), Name = "Mine", Currency = "RON", UserId = meId });
        db.Subscriptions.Add(new SubscriptionItem { Id = Guid.NewGuid(), Name = "Theirs", Currency = "RON", UserId = Guid.NewGuid() });
        await db.SaveChangesAsync();

        // take far above the cap to also exercise clamping (must not throw)
        var result = await ControllerAs(db, meId).GetAll(skip: -5, take: 99999);
        var ok = Assert.IsType<OkObjectResult>(result);
        var paged = Assert.IsType<ExpenseTracker.Api.Dtos.PagedResult<ExpenseTracker.Api.Dtos.SubscriptionResponseDto>>(ok.Value);
        Assert.Equal(1, paged.Total);
    }
}
