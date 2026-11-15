using System.ComponentModel.DataAnnotations;

namespace ExpenseTracker.Api.Dtos;

public class AccountDto
{
    public string Username { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
    public string? PendingEmail { get; set; }
    public DateTime? PendingEmailExpiresAt { get; set; }
    public int EmailRetryAfterSeconds { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ChangePasswordDto
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 8)]
    public string NewPassword { get; set; } = string.Empty;
}

public class ChangeEmailDto
{
    [Required, StringLength(128)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;
}

public class ConfirmEmailChangeDto
{
    [Required, RegularExpression("^[0-9]{6}$")]
    public string Code { get; set; } = string.Empty;
}
