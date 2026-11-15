using ExpenseTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Services;

public static class SubscriptionCapacity
{
    public const int Maximum = 5000;

    public static Task LockAsync(AppDbContext db, Guid userId)
    {
        if (!db.Database.IsRelational()) return Task.CompletedTask;
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Account capacity requires a transaction.");
        // Same user-first lock ordering as credential/budget writes. Serialize
        // count + create/import across requests and API instances.
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE");
    }
}
