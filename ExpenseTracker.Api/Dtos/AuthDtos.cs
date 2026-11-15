using System.ComponentModel.DataAnnotations;

namespace ExpenseTracker.Api.Dtos;

public class RegisterDto : IValidatableObject
{
    [Required]
    [StringLength(50, MinimumLength = 3)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if ((Username?.Trim().Length ?? 0) < 3 || (Username?.Any(char.IsControl) ?? false))
            yield return new ValidationResult("Username-ul trebuie să aibă cel puțin 3 caractere după eliminarea spațiilor și nu poate conține caractere de control.", [nameof(Username)]);
    }
}

public class VerifyEmailDto
{
    [Required]
    [StringLength(50)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [StringLength(6, MinimumLength = 6)]
    [RegularExpression("^[0-9]{6}$")]
    public string Code { get; set; } = string.Empty;
}

public class ResendCodeDto
{
    [Required]
    [StringLength(50)]
    public string Username { get; set; } = string.Empty;
}

public class ForgotPasswordDto
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;
}

public class ResetPasswordDto
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(6, MinimumLength = 6)]
    [RegularExpression("^[0-9]{6}$")]
    public string Code { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    public string NewPassword { get; set; } = string.Empty;
}

public class LoginDto
{
    [Required]
    [StringLength(50)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [StringLength(128)]
    public string Password { get; set; } = string.Empty;
}

// Tokens now travel in httpOnly cookies, so the body only carries
// non-sensitive identity info the SPA needs for rendering.
public class UserResponseDto
{
    public string Username { get; set; } = string.Empty;
}
