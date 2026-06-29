using System.ComponentModel.DataAnnotations;

namespace ExpenseTracker.Api.Dtos;

public class RegisterDto
{
    [Required]
    [StringLength(50, MinimumLength = 3)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;
}

public class LoginDto
{
    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

// Tokens now travel in httpOnly cookies, so the body only carries
// non-sensitive identity info the SPA needs for rendering.
public class UserResponseDto
{
    public string Username { get; set; } = string.Empty;
}
