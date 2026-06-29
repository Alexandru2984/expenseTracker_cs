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

    public AuthController(AppDbContext db, AuthTokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    // POST /api/auth/register
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto)
    {
        if (Encoding.UTF8.GetByteCount(dto.Password) > 72)
            return BadRequest(new ProblemDetails
            {
                Title = "Parolă invalidă.",
                Detail = "Parola este prea lungă (maxim 72 de caractere)."
            });

        var trimmedUsername = dto.Username.Trim();
        var loweredUsername = trimmedUsername.ToLower();

        if (await _db.Users.AnyAsync(u => u.Username.ToLower() == loweredUsername))
            return Conflict(new ProblemDetails
            {
                Title = "Username indisponibil.",
                Detail = "Acest username este deja utilizat. Alege altul."
            });

        var user = new User
        {
            Username = trimmedUsername, // Păstrăm formatul original pentru afișare
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password)
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await IssueSessionAsync(user);
        return Ok(new UserResponseDto { Username = user.Username });
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

        await IssueSessionAsync(user);
        return Ok(new UserResponseDto { Username = user.Username });
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

        if (stored is null || stored.RevokedAt is not null || DateTime.UtcNow >= stored.ExpiresAt)
        {
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
