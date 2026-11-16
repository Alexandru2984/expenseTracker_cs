using System.ComponentModel.DataAnnotations;

namespace ExpenseTracker.Api.Dtos;
public class MfaPasswordDto
{
    [Required, StringLength(128)] public string CurrentPassword { get; set; } = "";
}
public sealed class MfaConfirmDto
{
    [Required] public Guid? SetupId { get; set; }
    [Required, RegularExpression("^[0-9]{6}$")] public string Code { get; set; } = "";
}
public sealed class MfaProofDto : MfaPasswordDto
{
    [Required, StringLength(30)] public string Code { get; set; } = "";
}
public sealed class MfaLoginDto
{
    [Required, RegularExpression("^[A-Za-z0-9_-]{43}$")] public string ChallengeToken { get; set; } = "";
    [Required, StringLength(30)] public string Code { get; set; } = "";
}
