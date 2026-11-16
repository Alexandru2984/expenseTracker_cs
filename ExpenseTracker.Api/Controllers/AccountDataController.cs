using System.Security.Claims;
using System.Security.Cryptography;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Infrastructure;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ExpenseTracker.Api.Controllers;

[ApiController,Authorize,Route("api/account/data")]
public sealed class AccountDataController(AppDbContext db,AuthTokenService tokens,VerificationService verification,
    MfaService mfa,AccountArchive archive,TimeProvider clock,ILogger<AccountDataController> logger):ControllerBase
{
    private Guid UserId=>Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier),out var id)?id:Guid.Empty;
    private Guid SessionId=>Guid.TryParse(User.FindFirstValue("session_id"),out var id)?id:Guid.Empty;
    private DateTime Now=>clock.GetUtcNow().UtcDateTime;
    private IActionResult Changed()=>Conflict(BrowserAccountContext.Changed());
    private static int Retry(User user,DateTime now)=>user.DataExportRequestedAt is { } at?Math.Max(0,(int)Math.Ceiling((at.AddMinutes(1)-now).TotalSeconds)):0;
    private bool Bound()=>Request.Headers[AuthTokenService.AccountContextHeader].Count==1
        &&tokens.MatchesAccountContext(Request,UserId);
    private Task<bool> SessionActive(CancellationToken ct)=>db.RefreshTokens.AsNoTracking().AnyAsync(t=>t.Id==SessionId
        &&t.UserId==UserId&&t.RevokedAt==null&&t.ExpiresAt>Now&&!t.TokenHash.StartsWith("finance:"),ct);
    private async Task<IDbContextTransaction?> Begin(CancellationToken ct)
    {
        if(!db.Database.IsRelational())return null;
        var tx=await db.Database.BeginTransactionAsync(ct);
        try{await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '30s';",ct);return tx;}
        catch{await tx.DisposeAsync();throw;}
    }
    private async Task<User?> Locked(CancellationToken ct)
    {
        var user=await db.Users.FindAsync(new object[]{UserId},ct);if(user is null)return null;
        try{await verification.LockAccountAsync(user);return user;}
        catch(DbUpdateConcurrencyException){return null;}
    }
    private async Task<IActionResult?> Proof(User user,AccountDataProofDto request,IDbContextTransaction? tx,CancellationToken ct)
    {
        if(LoginProtection.IsBlocked(user,Now))return BadRequest(new ProblemDetails{Title="Parolă sau cod incorect. Așteaptă înainte de a reîncerca."});
        var valid=PasswordRules.Verify(request.CurrentPassword,user.PasswordHash);
        try{if(valid&&user.MfaEnabled)valid=await mfa.Proof(user,request.Code??"",ct);}
        catch(Exception ex)when(ex is CryptographicException or FormatException)
        {return StatusCode(503,new ProblemDetails{Title="Autentificatorul este indisponibil. Poți folosi un cod de recuperare MFA."});}
        if(!valid)
        {
            LoginProtection.Fail(user,Now);await db.SaveChangesAsync(ct);
            if(tx is not null)await tx.CommitAsync(ct);
            return BadRequest(new ProblemDetails{Title="Parolă sau cod MFA incorect, expirat ori deja folosit."});
        }
        LoginProtection.Reset(user);return null;
    }

    [HttpGet]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        if(!Bound())return Changed();
        var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(u=>u.Id==UserId,ct);if(user is null)return Unauthorized();
        return Ok(new {user.Username,mfaEnabled=user.MfaEnabled,exportRetryAfterSeconds=Retry(user,Now),archiveMaxBytes=ArchiveBuffer.MaximumBytes,
            subscriptions=await db.Subscriptions.CountAsync(s=>s.UserId==UserId,ct),
            priceChanges=await db.SubscriptionPriceChanges.CountAsync(p=>p.Subscription.UserId==UserId,ct),
            budgets=await db.Budgets.CountAsync(b=>b.UserId==UserId,ct),
            goals=await db.SavingsGoals.CountAsync(g=>g.UserId==UserId,ct),
            contributions=await db.GoalContributions.CountAsync(c=>c.Goal.UserId==UserId,ct),
            financeTokens=await db.RefreshTokens.CountAsync(t=>t.UserId==UserId&&t.TokenHash.StartsWith("finance:")&&t.RevokedAt==null&&t.ExpiresAt>Now,ct)});
    }

    [HttpPost("export"),EnableRateLimiting("auth")]
    public async Task<IActionResult> Export(AccountDataProofDto request,CancellationToken ct)
    {
        if(!Bound())return Changed();
        var buffer=AccountArchive.TryBegin();
        if(buffer is null){Response.Headers.RetryAfter="10";return StatusCode(429,new ProblemDetails{Title="Alte arhive sunt în pregătire. Reîncearcă în câteva secunde."});}
        var handedOff=false;
        try
        {
            string credentialStamp;
            await using(var tx=await Begin(ct))
            {
                var user=await Locked(ct);if(user is null)return Changed();
                if(!await SessionActive(ct))return Unauthorized();
                var retry=Retry(user,Now);
                if(retry>0){Response.Headers.RetryAfter=retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return StatusCode(429,new ProblemDetails{Title="Poți cere o arhivă nouă o dată pe minut."});}
                var failure=await Proof(user,request,tx,ct);if(failure is not null)return failure;
                user.DataExportRequestedAt=Now;credentialStamp=tokens.CredentialStamp(user);await db.SaveChangesAsync(ct);
                if(tx is not null)await tx.CommitAsync(ct);
            }
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try{await archive.Write(UserId,SessionId,credentialStamp,buffer,timeout.Token);}
            catch(ArchiveTooLargeException){return StatusCode(413,new ProblemDetails{Title="Arhiva depășește 16 MiB. Nu s-a descărcat un fișier parțial. Exportă abonamentele în CSV pe pagini și contactează operatorul pentru arhiva completă."});}
            catch(ArchiveAccountMissingException){return Changed();}
            catch(OperationCanceledException)when(!ct.IsCancellationRequested){return StatusCode(503,new ProblemDetails{Title="Pregătirea arhivei a durat prea mult. Reîncearcă mai târziu."});}
            handedOff=true;
            return File(buffer,"application/json",$"expense-tracker-account-{Now:yyyy-MM-dd}.json",enableRangeProcessing:false);
        }
        finally{if(!handedOff)await buffer.DisposeAsync();}
    }

    [HttpPost("delete"),EnableRateLimiting("auth")]
    public async Task<IActionResult> Delete(DeleteAccountDto request,CancellationToken ct)
    {
        if(!Bound())return Changed();
        await using var tx=await Begin(ct);
        var user=await Locked(ct);if(user is null)return Changed();
        if(!await SessionActive(ct))return Unauthorized();
        if(!string.Equals(request.ConfirmationUsername,user.Username,StringComparison.Ordinal))
            return BadRequest(new ProblemDetails{Title="Scrie exact numele contului pentru a confirma ștergerea."});
        var failure=await Proof(user,request,tx,ct);if(failure is not null)return failure;
        var deletedAt=Now;
        // In-memory EF has no database FK engine. Production deletion is a single
        // Users DELETE and PostgreSQL cascades all owned rows in this transaction.
        if(!db.Database.IsRelational())await TrackOwnedRows(ct);
        db.AccountDeletionRecords.Add(new AccountDeletionRecord{UserId=user.Id,DeletedAt=deletedAt});
        db.Users.Remove(user);await db.SaveChangesAsync(ct);
        if(tx is not null)await tx.CommitAsync(ct);
        tokens.ClearAuthCookies(Response,Request.IsHttps);
        logger.LogWarning("Account deletion committed for {AccountId} at {DeletedAt}",UserId,deletedAt);
        return Ok(new {deleted=true,deletedAt});
    }
    private async Task TrackOwnedRows(CancellationToken ct)
    {
        await db.Subscriptions.Where(s=>s.UserId==UserId).LoadAsync(ct);
        await db.SubscriptionPriceChanges.Where(p=>p.Subscription.UserId==UserId).LoadAsync(ct);
        await db.SavingsGoals.Where(g=>g.UserId==UserId).LoadAsync(ct);
        await db.GoalContributions.Where(c=>c.Goal.UserId==UserId).LoadAsync(ct);
        await db.Budgets.Where(b=>b.UserId==UserId).LoadAsync(ct);
        await db.RefreshTokens.Where(t=>t.UserId==UserId).LoadAsync(ct);
        await db.VerificationCodes.Where(c=>c.UserId==UserId).LoadAsync(ct);
        await db.MfaRecoveryCodes.Where(c=>c.UserId==UserId).LoadAsync(ct);
        await db.MfaLoginChallenges.Where(c=>c.UserId==UserId).LoadAsync(ct);
        await db.NotificationPreferences.Where(p=>p.UserId==UserId).LoadAsync(ct);
        await db.NotificationOutbox.Where(n=>n.UserId==UserId).LoadAsync(ct);
    }
}
