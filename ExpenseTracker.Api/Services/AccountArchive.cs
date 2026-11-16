using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpenseTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Services;

public sealed class ArchiveTooLargeException : Exception;
public sealed class ArchiveAccountMissingException : Exception;

// An archive is assembled before sending headers: a size/error never produces a partial download.
// No filesystem or background export URL. Two buffers, each capped at 16 MiB per process.
public sealed class ArchiveBuffer(Action release) : MemoryStream
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    private int released;
    private void Check(int count)
    {
        if (count < 0 || Position + count > MaximumBytes) throw new ArchiveTooLargeException();
    }
    public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer,offset,count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
    public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken ct)
    { Check(count); return base.WriteAsync(buffer,offset,count,ct); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default)
    { Check(buffer.Length); return base.WriteAsync(buffer,ct); }
    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { if (Interlocked.Exchange(ref released,1)==0) release(); }
    }
}

public sealed class AccountArchive(AppDbContext db,TimeProvider clock,AuthTokenService tokens)
{
    private static readonly SemaphoreSlim Slots = new(2,2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    public static ArchiveBuffer? TryBegin() => Slots.Wait(0) ? new ArchiveBuffer(()=>Slots.Release()) : null;

    public async Task Write(Guid userId,Guid sessionId,string expectedStamp,ArchiveBuffer destination,CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var transaction=db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct):null;
        if(transaction is not null)
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '30s';",ct);
        var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(u=>u.Id==userId,ct);
        if(user is null||tokens.CredentialStamp(user)!=expectedStamp||!await db.RefreshTokens.AsNoTracking()
            .AnyAsync(t=>t.Id==sessionId&&t.UserId==userId&&t.RevokedAt==null&&t.ExpiresAt>clock.GetUtcNow().UtcDateTime,ct))
            throw new ArchiveAccountMissingException();
        var profile=new {user.Id,user.Username,user.Email,user.EmailVerified,user.PendingEmail,user.CreatedAt,user.DataExportRequestedAt,user.MfaEnabled};
        using var writer=new Utf8JsonWriter(destination);
        writer.WriteStartObject();writer.WriteString("format","expense-tracker-account");writer.WriteNumber("schemaVersion",1);
        writer.WriteString("exportedAtUtc",clock.GetUtcNow());writer.WritePropertyName("profile");JsonSerializer.Serialize(writer,profile,Json);
        writer.WriteString("notice","Arhivă privată. Nu conține parole, tokenuri, coduri de email, chei sau coduri MFA. Nu este un backup de autentificare și nu se importă automat în Finance F#.");
        await writer.FlushAsync(ct);
        await Section("subscriptions",db.Subscriptions.AsNoTracking().Where(s=>s.UserId==userId).OrderBy(s=>s.Id)
            .Select(s=>new {s.Id,s.ImportSourceId,s.Name,s.Cost,s.Currency,s.BillingPeriod,s.NextBillingDate,s.Category,s.IsActive,s.Notes,s.ProviderUrl,s.TrialEndsOn,s.CreatedAt,s.UpdatedAt}),writer,ct);
        await Section("priceHistory",db.SubscriptionPriceChanges.AsNoTracking().Where(p=>p.Subscription.UserId==userId).OrderBy(p=>p.SubscriptionId).ThenBy(p=>p.ChangedAt).ThenBy(p=>p.Id)
            .Select(p=>new {p.Id,p.SubscriptionId,p.ChangedAt,p.PreviousCost,p.PreviousCurrency,p.PreviousBillingPeriod,p.Cost,p.Currency,p.BillingPeriod}),writer,ct);
        await Section("budgets",db.Budgets.AsNoTracking().Where(b=>b.UserId==userId).OrderBy(b=>b.Id)
            .Select(b=>new {b.Id,b.Currency,b.Category,b.MonthlyLimit,b.CreatedAt,b.UpdatedAt}),writer,ct);
        await Section("goals",db.SavingsGoals.AsNoTracking().Where(g=>g.UserId==userId).OrderBy(g=>g.Id)
            .Select(g=>new {g.Id,g.Name,g.Description,g.Currency,g.TargetAmount,g.MonthlyContribution,g.TargetDate,g.IsArchived,g.CreatedAt,g.UpdatedAt}),writer,ct);
        await Section("contributions",db.GoalContributions.AsNoTracking().Where(c=>c.Goal.UserId==userId).OrderBy(c=>c.GoalId).ThenBy(c=>c.ContributedOn).ThenBy(c=>c.Id)
            .Select(c=>new {c.Id,c.GoalId,c.Amount,c.ContributedOn,c.Note,c.CreatedAt}),writer,ct);
        await Section("notificationPreferences",db.NotificationPreferences.AsNoTracking().Where(p=>p.UserId==userId)
            .Select(p=>new {p.Enabled,p.TimeZone,p.SendHour,p.LeadDays,p.Renewals,p.Trials,p.GoalDeadlines,p.IncludeNames,p.ConsentRecordedAt,p.NextDigestAtUtc,p.UpdatedAt}),writer,ct);
        await Section("notificationHistory",db.NotificationOutbox.AsNoTracking().Where(n=>n.UserId==userId).OrderBy(n=>n.CreatedAt).ThenBy(n=>n.Id)
            .Select(n=>new {n.Id,n.LocalDate,n.TimeZone,n.State,n.Attempts,n.ErrorCode,n.CreatedAt,n.CompletedAt}),writer,ct);
        await Section("accessHistory",db.RefreshTokens.AsNoTracking().Where(t=>t.UserId==userId).OrderBy(t=>t.CreatedAt).ThenBy(t=>t.Id)
            .Select(t=>new {kind=t.TokenHash.StartsWith("finance:")?"finance-read-token":"browser-session",t.CreatedAt,t.ExpiresAt,t.RevokedAt,t.WasRotated}),writer,ct);
        writer.WriteEndObject();await writer.FlushAsync(ct);
        if(transaction is not null)await transaction.CommitAsync(ct);
        destination.Position=0;
    }
    private static async Task Section<T>(string name,IQueryable<T> query,Utf8JsonWriter writer,CancellationToken ct)
    {
        writer.WritePropertyName(name);writer.WriteStartArray();
        await foreach(var row in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            JsonSerializer.Serialize(writer,row,Json);
            if(writer.BytesPending>=16_384)await writer.FlushAsync(ct);
        }
        writer.WriteEndArray();await writer.FlushAsync(ct);
    }
}
