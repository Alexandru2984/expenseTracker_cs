using System.Security.Claims;
using System.Security.Cryptography;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ExpenseTracker.Api.Controllers;

[ApiController, Authorize, Route("api/account/mfa")]
public sealed class MfaController(AppDbContext db, VerificationService verification, MfaService mfa,
    MfaSecrets secrets, TimeProvider clock) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier),out var id) ? id : Guid.Empty;
    private IActionResult Invalid() => BadRequest(new ProblemDetails { Title = "Parolă sau cod incorect. Folosește un cod nou ori un cod de rezervă nefolosit." });
    private IActionResult Unavailable() => StatusCode(503,new ProblemDetails { Title = "Verificarea MFA este indisponibilă. Poți încerca un cod de rezervă." });
    private IActionResult Changed() => Conflict(new ProblemDetails { Title = "Starea contului s-a schimbat. Reîncarcă setările." });
    private async Task<User?> Locked(CancellationToken ct)
    {
        var user = await db.Users.FindAsync(new object[] { UserId },ct);
        if (user is null) return null;
        try { await verification.LockAccountAsync(user); return user; }
        catch (DbUpdateConcurrencyException) { return null; }
    }
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u=>u.Id==UserId,ct);
        if(user is null)return Unauthorized();
        var now=clock.GetUtcNow().UtcDateTime;
        return Ok(new { enabled=user.MfaEnabled,available=secrets.Available,
            recoveryCodesRemaining=await db.MfaRecoveryCodes.CountAsync(c=>c.UserId==UserId&&c.ConsumedAt==null,ct),
            pendingSetupId=user.MfaPendingExpiresAt>now?user.MfaSetupId:null,
            pendingExpiresAt=user.MfaPendingExpiresAt>now?user.MfaPendingExpiresAt:null });
    }
    [HttpPost("setup"),EnableRateLimiting("auth")]
    public async Task<IActionResult> Setup(MfaPasswordDto request,CancellationToken ct)
    {
        if(!secrets.Available)return Unavailable();
        await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(ct):null;
        var user=await Locked(ct);if(user is null)return Changed();
        if(user.MfaEnabled)return Conflict(new ProblemDetails {Title="MFA este deja activată."});
        if(!user.EmailVerified)return BadRequest(new ProblemDetails {Title="Verifică adresa de email înainte de a configura MFA."});
        if(!PasswordRules.Verify(request.CurrentPassword,user.PasswordHash))return Invalid();
        var now=clock.GetUtcNow().UtcDateTime;
        if(user.MfaSetupStartedAt>now.AddSeconds(-60))
        {
            Response.Headers.RetryAfter="60";return StatusCode(429,new ProblemDetails {Title="Așteaptă un minut înainte de a reîncepe configurarea."});
        }
        var secret=Totp.NewSecret();
        user.MfaSetupId=Guid.NewGuid();user.MfaPendingSecret=secrets.Protect(user.Id,secret);
        user.MfaSetupStartedAt=now;user.MfaPendingExpiresAt=now.AddMinutes(10);user.MfaSetupAttempts=0;
        await db.SaveChangesAsync(ct);if(tx is not null)await tx.CommitAsync(ct);
        var uri="otpauth://totp/"+Uri.EscapeDataString("Expense Tracker:"+user.Username)
            +"?secret="+secret+"&issuer=Expense%20Tracker&algorithm=SHA1&digits=6&period=30";
        return Ok(new {setupId=user.MfaSetupId,secret,otpauthUri=uri,expiresAt=user.MfaPendingExpiresAt});
    }
    [HttpDelete("setup"),EnableRateLimiting("writes")]
    public async Task<IActionResult> Cancel([FromQuery]Guid setupId,CancellationToken ct)
    {
        await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(ct):null;
        var user=await Locked(ct);if(user is null)return Changed();
        if(user.MfaSetupId!=setupId)return Changed();
        MfaService.ClearSetup(user);await db.SaveChangesAsync(ct);if(tx is not null)await tx.CommitAsync(ct);
        return NoContent();
    }
    [HttpPost("confirm"),EnableRateLimiting("auth")]
    public async Task<IActionResult> Confirm(MfaConfirmDto request,CancellationToken ct)
    {
        await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(ct):null;
        var user=await Locked(ct);if(user is null)return Changed();
        if(user.MfaEnabled||user.MfaSetupId!=request.SetupId||user.MfaPendingExpiresAt<=mfa.Now
            ||user.MfaPendingSecret is null||user.MfaSetupAttempts>=5)return Changed();
        long? step;
        try{step=Totp.Verify(secrets.Unprotect(user.Id,user.MfaPendingSecret),request.Code,clock.GetUtcNow(),null);}
        catch(Exception ex)when(ex is CryptographicException or FormatException){return Unavailable();}
        if(step is null)
        {
            user.MfaSetupAttempts++;
            if(user.MfaSetupAttempts>=5)MfaService.ClearSetup(user);
            await db.SaveChangesAsync(ct);if(tx is not null)await tx.CommitAsync(ct);return Invalid();
        }
        user.MfaEnabled=true;user.MfaEpoch=Guid.NewGuid();user.MfaSecret=user.MfaPendingSecret;user.MfaLastStep=step;
        user.PasswordHash=PasswordRules.MfaPrefix+PasswordRules.WithoutMfa(user.PasswordHash);
        LoginProtection.Reset(user);
        MfaService.ClearSetup(user);await verification.InvalidateAllAsync(user.Id);
        await mfa.RevokeBrowserSessions(user,ct);var recoveryCodes=await mfa.NewRecovery(user,ct);
        var write=await mfa.NewSession(user,Response,Request.IsHttps,ct);
        if(tx is not null)await tx.CommitAsync(ct);write();
        return Ok(new {recoveryCodes});
    }
    [HttpPost("recovery-codes"),EnableRateLimiting("auth")]
    public async Task<IActionResult> Recovery(MfaProofDto request,CancellationToken ct)
        =>await Manage(request,false,ct);
    [HttpPost("disable"),EnableRateLimiting("auth")]
    public async Task<IActionResult> Disable(MfaProofDto request,CancellationToken ct)
        =>await Manage(request,true,ct);
    private async Task<IActionResult> Manage(MfaProofDto request,bool disable,CancellationToken ct)
    {
        await using var tx=db.Database.IsRelational()?await db.Database.BeginTransactionAsync(ct):null;
        var user=await Locked(ct);if(user is null)return Changed();
        if(!user.MfaEnabled)return Changed();
        if(LoginProtection.IsBlocked(user,mfa.Now))return Invalid();
        var valid=PasswordRules.Verify(request.CurrentPassword,user.PasswordHash);
        try{valid=valid&&await mfa.Proof(user,request.Code,ct);}
        catch(Exception ex)when(ex is CryptographicException or FormatException){return Unavailable();}
        if(!valid)
        {
            LoginProtection.Fail(user,mfa.Now);await db.SaveChangesAsync(ct);
            if(tx is not null)await tx.CommitAsync(ct);return Invalid();
        }
        LoginProtection.Reset(user);
        if(!disable)
        {
            var recoveryCodes=await mfa.NewRecovery(user,ct);await db.SaveChangesAsync(ct);
            if(tx is not null)await tx.CommitAsync(ct);return Ok(new {recoveryCodes});
        }
        user.MfaEnabled=false;user.MfaEpoch=Guid.NewGuid();user.MfaSecret=null;user.MfaLastStep=null;
        user.PasswordHash=PasswordRules.WithoutMfa(user.PasswordHash);MfaService.ClearSetup(user);
        await mfa.RevokeBrowserSessions(user,ct);
        db.MfaRecoveryCodes.RemoveRange(await db.MfaRecoveryCodes.Where(c=>c.UserId==user.Id).ToListAsync(ct));
        var write=await mfa.NewSession(user,Response,Request.IsHttps,ct);
        if(tx is not null)await tx.CommitAsync(ct);write();return NoContent();
    }
}
