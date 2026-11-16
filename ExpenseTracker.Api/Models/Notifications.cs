namespace ExpenseTracker.Api.Models;

public class NotificationPreference
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public bool Enabled { get; set; }
    public string TimeZone { get; set; } = "Europe/Bucharest";
    public int SendHour { get; set; } = 9;
    public int LeadDays { get; set; } = 7;
    public bool Renewals { get; set; } = true;
    public bool Trials { get; set; } = true;
    public bool GoalDeadlines { get; set; }
    public bool IncludeNames { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTime? ConsentRecordedAt { get; set; }
    public DateTime? NextDigestAtUtc { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum NotificationState { Pending, Preparing, Sending, Sent, Failed, Canceled, Uncertain }

public class NotificationOutbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateOnly LocalDate { get; set; }
    public string TimeZone { get; set; } = "Europe/Bucharest";
    public Guid PreferenceVersion { get; set; }
    public string RecipientHash { get; set; } = "";
    public NotificationState State { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? LeaseId { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public string ErrorCode { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
