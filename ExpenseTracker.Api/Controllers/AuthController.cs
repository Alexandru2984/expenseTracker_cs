using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ExpenseTracker.Api.Data;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AuthController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    // POST /api/auth/register
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto)
    {
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

        var response = GenerateToken(user);
        return Ok(response);
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

        var response = GenerateToken(user);
        return Ok(response);
    }

    private AuthResponseDto GenerateToken(User user)
    {
        var jwtSecret = _config["Jwt:Secret"]!;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expiry = DateTime.UtcNow.AddHours(2); // Redus de la AddDays(30) la 2 ore

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username)
        };

        var token = new JwtSecurityToken(
            issuer: "ExpenseTracker",
            audience: "ExpenseTracker",
            claims: claims,
            expires: expiry,
            signingCredentials: creds
        );

        return new AuthResponseDto
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            Username = user.Username,
            ExpiresAt = expiry
        };
    }
}
