using System.ComponentModel.DataAnnotations;
namespace ExpenseTracker.Api.Dtos;

public class AccountDataProofDto
{
    [Required, StringLength(128)] public string CurrentPassword { get; set; } = "";
    [StringLength(30)] public string? Code { get; set; }
}
public sealed class DeleteAccountDto : AccountDataProofDto
{
    [Required, StringLength(50)] public string ConfirmationUsername { get; set; } = "";
}
