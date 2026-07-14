using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;
using ExpenseTracker.Api.Services;

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

    public AuthController(AppDbContext db, AuthTokenService tokens, VerificationService verification, IConfiguration config)
    {
        _db = db;
        _tokens = tokens;
        _verification = verification;
        _config = config;
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
                Detail = "Parola este prea lungă (maxim 72 de caractere)."
            });

        var trimmedUsername = dto.Username.Trim();
        var loweredUsername = trimmedUsername.ToLower();
        var email = dto.Email.Trim().ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Username.ToLower() == loweredUsername))
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
        await _db.SaveChangesAsync();

        await _verification.IssueAsync(user, VerificationPurpose.EmailVerification);

        return Ok(new
        {
            username = user.Username,
            email = user.Email,
            requiresVerification = true,
            message = "Cont creat. Ți-am trimis un cod de verificare pe email."
        });
    }

    // POST /api/auth/login
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        var loweredUsername = dto.Username.Trim().ToLower();
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Username.ToLower() == loweredUsername);

        if (user is null || !BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
            return Unauthorized(new ProblemDetails
            {
                Title = "Autentificare eșuată.",
                Detail = "Username sau parolă greșită."
            });

        if (!user.EmailVerified)
        {
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

        await IssueSessionAsync(user);
        return Ok(new UserResponseDto { Username = user.Username });
    }

    // POST /api/auth/verify-email — confirms the code and auto-logs the user in.
    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail([FromBody] VerifyEmailDto dto)
    {
        var lowered = dto.Username.Trim().ToLower();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == lowered);
        if (user is null)
            return BadRequest(InvalidCode());

        if (!user.EmailVerified)
        {
            if (!await _verification.ConsumeAsync(user, VerificationPurpose.EmailVerification, dto.Code))
                return BadRequest(InvalidCode());

            user.EmailVerified = true;
            await _db.SaveChangesAsync();
        }

        await IssueSessionAsync(user);
        return Ok(new UserResponseDto { Username = user.Username });
    }

    // POST /api/auth/resend-code — re-sends an email-verification code.
    [HttpPost("resend-code")]
    public async Task<IActionResult> ResendCode([FromBody] ResendCodeDto dto)
    {
        var lowered = dto.Username.Trim().ToLower();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == lowered);
        if (user is not null && !user.EmailVerified && user.Email is not null)
            await _verification.IssueAsync(user, VerificationPurpose.EmailVerification);

        // Never reveal whether the account exists / is already verified.
        return Ok(new { message = "Dacă există un cont neverificat, ți-am trimis un cod nou." });
    }

    // POST /api/auth/forgot-password — emails a reset code.
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto)
    {
        var email = dto.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is not null && user.Email is not null)
            await _verification.IssueAsync(user, VerificationPurpose.PasswordReset);

        return Ok(new { message = "Dacă există un cont cu acest email, ți-am trimis un cod de resetare." });
    }

    // POST /api/auth/reset-password — sets a new password using the reset code.
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto)
    {
        if (Encoding.UTF8.GetByteCount(dto.NewPassword) > 72)
            return BadRequest(new ProblemDetails
            {
                Title = "Parolă invalidă.",
                Detail = "Parola este prea lungă (maxim 72 de caractere)."
            });

        var email = dto.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user is null || !await _verification.ConsumeAsync(user, VerificationPurpose.PasswordReset, dto.Code))
            return BadRequest(InvalidCode());

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
        user.EmailVerified = true; // proving control of the inbox also verifies it

        // Revoke every active session (logout everywhere) after a password reset.
        var tokens = await _db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ToListAsync();
        foreach (var t in tokens) t.RevokedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { message = "Parola a fost resetată. Te poți autentifica acum." });
    }

    // POST /api/auth/refresh — rotates the refresh token and re-issues the access cookie
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh()
    {
        var raw = Request.Cookies[AuthTokenService.RefreshCookie];
        if (string.IsNullOrEmpty(raw))
            return Unauthorized();

        var hash = AuthTokenService.Hash(raw);
        var stored = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (stored is null || DateTime.UtcNow >= stored.ExpiresAt)
        {
            _tokens.ClearAuthCookies(Response, Request.IsHttps);
            return Unauthorized();
        }

        // Reuse detection: presenting an already-rotated (revoked) token means the
        // token was captured and replayed. Treat it as a compromise and revoke
        // every active session for the user, forcing a fresh login.
        if (stored.RevokedAt is not null)
        {
            var family = await _db.RefreshTokens
                .Where(t => t.UserId == stored.UserId && t.RevokedAt == null)
                .ToListAsync();
            foreach (var t in family) t.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _tokens.ClearAuthCookies(Response, Request.IsHttps);
            return Unauthorized();
        }

        // Rotate: revoke the presented token, issue a fresh pair.
        stored.RevokedAt = DateTime.UtcNow;
        await IssueSessionAsync(stored.User);
        return Ok(new UserResponseDto { Username = stored.User.Username });
    }

    // POST /api/auth/logout — revokes the current refresh token and clears cookies
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        var raw = Request.Cookies[AuthTokenService.RefreshCookie];
        if (!string.IsNullOrEmpty(raw))
        {
            var hash = AuthTokenService.Hash(raw);
            var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (stored is not null && stored.RevokedAt is null)
            {
                stored.RevokedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }

        _tokens.ClearAuthCookies(Response, Request.IsHttps);
        return NoContent();
    }

    // GET /api/auth/me — current identity (used by the SPA to bootstrap auth state)
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        var username = User.FindFirstValue(ClaimTypes.Name) ?? "";
        return Ok(new UserResponseDto { Username = username });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProblemDetails InvalidCode() => new()
    {
        Title = "Cod invalid.",
        Detail = "Codul este greșit sau a expirat. Cere unul nou."
    };

    // Creates+persists a rotating refresh token, writes both auth cookies.
    private async Task IssueSessionAsync(User user)
    {
        var (accessToken, accessExpires) = _tokens.CreateAccessToken(user);
        var (rawRefresh, refreshHash, refreshExpires) = _tokens.CreateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshHash,
            ExpiresAt = refreshExpires
        });
        await _db.SaveChangesAsync();

        _tokens.WriteAuthCookies(Response, Request.IsHttps,
            accessToken, accessExpires, rawRefresh, refreshExpires);
    }
}
