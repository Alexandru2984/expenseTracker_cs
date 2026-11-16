using System.ComponentModel.DataAnnotations;
using ExpenseTracker.Api.Services;

namespace ExpenseTracker.Api.Dtos;

public class SaveNotificationPreferenceDto : IValidatableObject
{
    [Required] public Guid? Version { get; set; }
    public bool Enabled { get; set; }
    [Required, StringLength(100)] public string TimeZone { get; set; } = "Europe/Bucharest";
    [Range(0, 23)] public int SendHour { get; set; } = 9;
    [Range(0, 14)] public int LeadDays { get; set; } = 7;
    public bool Renewals { get; set; } = true;
    public bool Trials { get; set; } = true;
    public bool GoalDeadlines { get; set; }
    public bool IncludeNames { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (NotificationSchedule.Zone(TimeZone) is null)
            yield return new ValidationResult("Alege un fus orar IANA disponibil.", [nameof(TimeZone)]);
        if (Enabled && !Renewals && !Trials && !GoalDeadlines)
            yield return new ValidationResult("Selectează cel puțin un tip de notificare.", [nameof(Enabled)]);
    }
}

public sealed record NotificationItemDto(string Kind, string Name, DateOnly Date);
public sealed record NotificationPreviewDto(DateOnly LocalDate, int LeadDays, int RenewalCount,
    int TrialCount, int GoalCount, IReadOnlyList<NotificationItemDto> Items)
{
    public bool HasItems => RenewalCount + TrialCount + GoalCount > 0;
}
