using System.Security.Claims;
using System.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;
using ExpenseTracker.Api.Infrastructure;

namespace ExpenseTracker.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuthTokenService _tokens;
    private readonly VerificationService _verification;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;
    private readonly MfaService _mfa;
    private static readonly string DummyPasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"));

    public AuthController(AppDbContext db, AuthTokenService tokens, VerificationService verification, IConfiguration config, ILogger<AuthController> logger, MfaService mfa)
    {
        _db = db;
        _tokens = tokens;
        _verification = verification;
        _config = config;
        _logger = logger;
        _mfa = mfa;
    }

    // POST /api/auth/register — creates an UNVERIFIED account and emails a code.
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto)
    {
        if (!_config.GetValue("Auth:AllowRegistration", true))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Înregistrări dezactivate.",
                Detail = "Crearea de conturi noi este momentan dezactivată."
            });

        if (Encoding.UTF8.GetByteCount(dto.Password) > 72)
            return BadRequest(new ProblemDetails
            {
                Title = "Parolă invalidă.",
                Detail = "Parola depășește limita de 72 de octeți UTF-8."
            });

        var trimmedUsername = dto.Username.Trim();
        var email = dto.Email.Trim().ToLowerInvariant();

        if (await _db.FindUserByUsernameAsync(trimmedUsername) is not null)
            return Conflict(new ProblemDetails
            {
                Title = "Username indisponibil.",
                Detail = "Acest username este deja utilizat. Alege altul."
            });

        if (await _db.Users.AnyAsync(u => u.Email == email))
            return Conflict(new ProblemDetails
            {
                Title = "Email indisponibil.",
                Detail = "Există deja un cont cu acest email."
            });

        var user = new User
        {
            Username = trimmedUsername, // Păstrăm formatul original pentru afișare
            Email = email,
            EmailVerified = false,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password)
        };

        _db.Users.Add(user);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return Conflict(new ProblemDetails { Title = "Date indisponibile.", Detail = "Username-ul sau emailul este deja utilizat." });
        }

        await _verification.IssueAsync(user, VerificationPurpose.EmailVerification);

        return Ok(new
        {
            username = user.Username,
            email = user.Email,
            requiresVerification = true,
            retryAfterSeconds = VerificationService.EmailCooldownSeconds,
            message = "Cont creat. Ți-am trimis un cod de verificare pe email."
        });
    }

    // POST /api/auth/login
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        var user = await _db.FindUserByUsernameAsync(dto.Username);

        if (user is null)
        {
            // Keep an unknown account on the BCrypt path too. Never persist attacker-chosen names.
            PasswordRules.Verify(dto.Password, DummyPasswordHash);
            return Unauthorized(LoginFailed());
        }

        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
        await _verification.LockAccountAsync(user);
        var now = DateTime.UtcNow;
        if (LoginProtection.IsBlocked(user, now))
        {
            if (transaction is not null) await transaction.CommitAsync();
            PasswordRules.Verify(dto.Password, DummyPasswordHash);
            return Unauthorized(LoginFailed());
        }
        if (!PasswordRules.Verify(dto.Password, user.PasswordHash))
        {
            LoginProtection.Fail(user, now);
            await _db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            if (LoginProtection.IsBlocked(user, now))
                _logger.LogWarning("Account login backoff activated for {AccountId} until {BlockedUntil}", user.Id, user.LoginBlockedUntil);
            return Unauthorized(LoginFailed());
        }

        if (!user.EmailVerified)
        {
            LoginProtection.Reset(user);await _db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            var pd = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Email neverificat.",
                Detail = "Confirmă-ți adresa de email pentru a te putea autentifica."
            };
            pd.Extensions["emailVerificationRequired"] = true;
            pd.Extensions["username"] = user.Username;
            return StatusCode(StatusCodes.Status403Forbidden, pd);
        }

        if (user.MfaEnabled)
        {
            var challenge=await _mfa.Challenge(user,Request.HttpContext.RequestAborted);
            if(transaction is not null)await transaction.CommitAsync();
            if(challenge is null)
            {
                Response.Headers.RetryAfter="60";
                return StatusCode(429,new ProblemDetails {Title="Ai deja verificări MFA în așteptare. Folosește una existentă sau reîncearcă mai târziu."});
            }
            return Accepted(challenge);
        }
        LoginProtection.Reset(user);
        await _db.SaveChangesAsync();
        var session = await CreateSessionAsync(user);
        if (transaction is not null) await transaction.CommitAsync();
        WriteSession(session);
        return Ok(new UserResponseDto { Username = user.Username, AccountContext = _tokens.AccountContext(user.Id) });
    }

    [HttpPost("mfa-login"),AllowAnonymous]
    public async Task<IActionResult> MfaLogin(MfaLoginDto request,CancellationToken ct)
    {
        var hash=MfaService.ChallengeHash(request.ChallengeToken);
        var challenge=await _db.MfaLoginChallenges.Include(c=>c.User).SingleOrDefaultAsync(c=>c.TokenHash==hash,ct);
        if(challenge is null)return Unauthorized(MfaRestart());
        await using var transaction=_db.Database.IsRelational()?await _db.Database.BeginTransactionAsync(ct):null;
        try {await _verification.LockAccountAsync(challenge.User);}
        catch(DbUpdateConcurrencyException){return Unauthorized(MfaRestart());}
        if(_db.Database.IsRelational())await _db.Entry(challenge).ReloadAsync(ct);
        var user=challenge.User;var now=_mfa.Now;
        if(challenge.ConsumedAt is not null||challenge.ExpiresAt<=now||challenge.Attempts>=5||!user.MfaEnabled
            ||!user.EmailVerified||challenge.CredentialStamp!=_tokens.CredentialStamp(user)||LoginProtection.IsBlocked(user,now))
            return Unauthorized(MfaRestart());
        bool valid;
        try{valid=await _mfa.Proof(user,request.Code,ct);}
        catch(Exception ex)when(ex is CryptographicException or FormatException)
        {return StatusCode(503,new ProblemDetails {Title="MFA este temporar indisponibilă. Poți folosi un cod de rezervă."});}
        if(!valid)
        {
            challenge.Attempts++;if(challenge.Attempts>=5)challenge.ConsumedAt=now;
            LoginProtection.Fail(user,now);await _db.SaveChangesAsync(ct);
            if(transaction is not null)await transaction.CommitAsync(ct);
            if(challenge.Attempts>=5)return Unauthorized(MfaRestart());
            return Unauthorized(new ProblemDetails {Title="Cod MFA incorect, expirat sau deja folosit. Încearcă un cod nou ori un cod de rezervă."});
        }
        challenge.ConsumedAt=now;LoginProtection.Reset(user);
        var session=await CreateSessionAsync(user);
        if(transaction is not null)await transaction.CommitAsync(ct);
        WriteSession(session);
        return Ok(new UserResponseDto {Username=user.Username,AccountContext=_tokens.AccountContext(user.Id)});
    }

    private static ProblemDetails MfaRestart()=>new(){Title="Verificarea MFA a expirat sau nu mai este valabilă. Reîncepe conectarea.",
        Extensions={{"code","mfa_restart_required"}}};

    [HttpPost("mfa-cancel"),AllowAnonymous]
    public async Task<IActionResult> MfaCancel(MfaLoginDto request,CancellationToken ct)
    {
        var hash=MfaService.ChallengeHash(request.ChallengeToken);
        var challenge=await _db.MfaLoginChallenges.Include(c=>c.User).SingleOrDefaultAsync(c=>c.TokenHash==hash,ct);
        if(challenge is null)return NoContent();
        await using var transaction=_db.Database.IsRelational()?await _db.Database.BeginTransactionAsync(ct):null;
        try{await _verification.LockAccountAsync(challenge.User);}
        catch(DbUpdateConcurrencyException){return NoContent();}
        if(_db.Database.IsRelational())await _db.Entry(challenge).ReloadAsync(ct);
        challenge.ConsumedAt??=_mfa.Now;await _db.SaveChangesAsync(ct);
        if(transaction is not null)await transaction.CommitAsync(ct);
        return NoContent();
    }

    // POST /api/auth/verify-email — confirms the code and auto-logs the user in.
    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailDto dto)
    {
        var user = await _db.FindUserByUsernameAsync(dto.Username);
        // Verification is a one-time proof, never a passwordless login route for
        // an account that was already verified (including after password reset).
        if (user is null || user.EmailVerified || user.MfaEnabled)
            return BadRequest(InvalidCode());

        if (!await _verification.ConsumeAsync(user, VerificationPurpose.EmailVerification, dto.Code))
            return BadRequest(InvalidCode());

        user.EmailVerified = true;
        await _db.SaveChangesAsync();

        await IssueSessionAsync(user);
        return Ok(new UserResponseDto { Username = user.Username, AccountContext = _tokens.AccountContext(user.Id) });
    }

    // POST /api/auth/resend-code — re-sends an email-verification code.
    [HttpPost("resend-code")]
    public async Task<IActionResult> ResendCode([FromBody] ResendCodeDto dto)
    {
        var user = await _db.FindUserByUsernameAsync(dto.Username);
        if (user is not null && !user.EmailVerified && user.Email is not null)
            await _verification.IssueAsync(user, VerificationPurpose.EmailVerification);

        // Never reveal whether the account exists / is already verified.
        return Ok(new { message = "Dacă există un cont neverificat, un cod poate fi trimis cel mult o dată pe minut.",
            retryAfterSeconds = VerificationService.EmailCooldownSeconds });
    }

    // POST /api/auth/forgot-password — emails a reset code.
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto)
    {
        var email = dto.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is not null && user.Email is not null)
            await _verification.IssueAsync(user, VerificationPurpose.PasswordReset);

        return Ok(new { message = "Dacă există un cont cu acest email, un cod poate fi trimis cel mult o dată pe minut.",
            retryAfterSeconds = VerificationService.EmailCooldownSeconds });
    }

    // POST /api/auth/reset-password — sets a new password using the reset code.
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto)
    {
        if (Encoding.UTF8.GetByteCount(dto.NewPassword) > 72)
            return BadRequest(new ProblemDetails
            {
                Title = "Parolă invalidă.",
                Detail = "Parola depășește limita de 72 de octeți UTF-8."
            });

        var email = dto.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null) return BadRequest(InvalidCode());
        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
        await _verification.LockAccountAsync(user);
        if (!await _verification.ConsumeAsync(user, VerificationPurpose.PasswordReset, dto.Code))
        {
            if (transaction is not null) await transaction.CommitAsync();
            return BadRequest(InvalidCode());
        }

        user.PasswordHash = PasswordRules.Hash(dto.NewPassword, user.MfaEnabled);
        LoginProtection.Reset(user);
        user.EmailVerified = true; // proving control of the inbox also verifies it
        await _verification.InvalidateAllAsync(user.Id);

        // Revoke every active session (logout everywhere) after a password reset.
        var tokens = await _db.RefreshTokens
            .Where(t => t.UserId == user.Id && (t.RevokedAt == null || t.WasRotated))
            .ToListAsync();
        foreach (var t in tokens) { t.RevokedAt ??= DateTime.UtcNow; t.WasRotated = false; }

        await _db.SaveChangesAsync();
        if (transaction is not null) await transaction.CommitAsync();
        return Ok(new { message = "Parola a fost resetată. Te poți autentifica acum." });
    }

    // POST /api/auth/refresh — rotates the refresh token and re-issues the access cookie
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh()
    {
        if (User.Identity?.IsAuthenticated == true && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var currentUser)
            && !_tokens.MatchesAccountContext(Request, currentUser)) return Conflict(BrowserAccountContext.Changed());
        var raw = Request.Cookies[AuthTokenService.RefreshCookie];
        if (string.IsNullOrEmpty(raw))
            return Unauthorized();

        var hash = AuthTokenService.Hash(raw);
        var stored = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (stored is not null && !_tokens.MatchesAccountContext(Request, stored.UserId))
            return Conflict(BrowserAccountContext.Changed());

        if (stored is null || !stored.User.EmailVerified || DateTime.UtcNow >= stored.ExpiresAt)
        {
            _tokens.ClearAuthCookies(Response, Request.IsHttps);
            return Unauthorized();
        }

        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
        try
        {
            await _verification.LockAccountAsync(stored.User);
            // Refresh may have waited behind a password change or another tab.
            await _db.Entry(stored).ReloadAsync();
            if (DateTime.UtcNow >= stored.ExpiresAt)
            {
                _tokens.ClearAuthCookies(Response, Request.IsHttps);
                return Unauthorized();
            }
            if (stored.RevokedAt is not null)
            {
                if (stored.WasRotated)
                {
                    var family = await _db.RefreshTokens.Where(t => t.UserId == stored.UserId
                        && (t.RevokedAt == null || t.WasRotated)).ToListAsync();
                    foreach (var token in family) { token.RevokedAt ??= DateTime.UtcNow; token.WasRotated = false; }
                    await _db.SaveChangesAsync();
                    if (transaction is not null) await transaction.CommitAsync();
                }
                _tokens.ClearAuthCookies(Response, Request.IsHttps);
                return Unauthorized();
            }
            stored.RevokedAt = DateTime.UtcNow;
            stored.WasRotated = true;
            var session = await CreateSessionAsync(stored.User);
            if (transaction is not null) await transaction.CommitAsync();
            WriteSession(session);
        }
        catch (DbUpdateConcurrencyException)
        {
            _tokens.ClearAuthCookies(Response, Request.IsHttps);
            return Unauthorized();
        }
        return Ok(new UserResponseDto { Username = stored.User.Username, AccountContext = _tokens.AccountContext(stored.UserId) });
    }

    // POST /api/auth/logout — revokes the current refresh token and clears cookies
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        if (User.Identity?.IsAuthenticated == true && Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var currentUser)
            && !_tokens.MatchesAccountContext(Request, currentUser)) return Conflict(BrowserAccountContext.Changed());
        var raw = Request.Cookies[AuthTokenService.RefreshCookie];
        if (!string.IsNullOrEmpty(raw))
        {
            var hash = AuthTokenService.Hash(raw);
            var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (stored is not null && !_tokens.MatchesAccountContext(Request, stored.UserId))
                return Conflict(BrowserAccountContext.Changed());
            if (stored is not null && stored.RevokedAt is null)
            {
                stored.RevokedAt = DateTime.UtcNow;
                stored.WasRotated = false;
                await _db.SaveChangesAsync();
            }
        }

        _tokens.ClearAuthCookies(Response, Request.IsHttps);
        return NoContent();
    }

    // GET /api/auth/me — current identity (used by the SPA to bootstrap auth state)
    [HttpGet("me")]
    [Authorize]
    [EnableRateLimiting("auth-status")]
    public IActionResult Me()
    {
        var username = User.FindFirstValue(ClaimTypes.Name) ?? "";
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        return Ok(new UserResponseDto { Username = username, AccountContext = _tokens.AccountContext(userId) });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProblemDetails InvalidCode() => new()
    {
        Title = "Cod invalid.",
        Detail = "Codul este greșit sau a expirat. Cere unul nou."
    };

    private static ProblemDetails LoginFailed() => new()
    {
        Title = "Autentificare eșuată.",
        Detail = "Date incorecte sau prea multe încercări. Verifică datele ori reîncearcă mai târziu."
    };

    // Creates+persists a rotating refresh token, writes both auth cookies.
    private async Task IssueSessionAsync(User user)
    {
        await using var transaction = _db.Database.IsRelational() ? await _db.Database.BeginTransactionAsync() : null;
        await _verification.LockAccountAsync(user);
        var session = await CreateSessionAsync(user);
        if (transaction is not null) await transaction.CommitAsync();
        WriteSession(session);
    }

    private async Task<(string access, DateTime accessExpires, string refresh, DateTime refreshExpires)> CreateSessionAsync(User user)
    {
        var (rawRefresh, refreshHash, refreshExpires) = _tokens.CreateRefreshToken();

        var session = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAt = refreshExpires
        };
        _db.RefreshTokens.Add(session);
        await _db.SaveChangesAsync();
        var (accessToken, accessExpires) = _tokens.CreateAccessToken(user, session.Id);

        return (accessToken, accessExpires, rawRefresh, refreshExpires);
    }
    private void WriteSession((string access, DateTime accessExpires, string refresh, DateTime refreshExpires) session) =>
        _tokens.WriteAuthCookies(Response, Request.IsHttps, session.access, session.accessExpires, session.refresh, session.refreshExpires);
}
