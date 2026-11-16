using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using ExpenseTracker.Api.Dtos;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Services;

public enum ReminderSendResult { Accepted, Retryable, Failed, Uncertain }
public interface IReminderEmailSender
{
    bool IsConfigured { get; }
    Task<ReminderSendResult> Send(MimeMessage message, CancellationToken ct);
}

public sealed class ReminderEmailSender(IConfiguration config) : IReminderEmailSender
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Smtp:Host"])
        && MailboxAddress.TryParse(config["Smtp:From"] ?? config["Smtp:User"], out _);
    public async Task<ReminderSendResult> Send(MimeMessage message, CancellationToken ct)
    {
        if (!IsConfigured) return ReminderSendResult.Retryable;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = new SmtpClient { Timeout = 20_000 };
        var sending = false;
        try
        {
            await client.ConnectAsync(config["Smtp:Host"]!, config.GetValue("Smtp:Port", 587),
                config.GetValue("Smtp:UseStartTls", true) ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, timeout.Token);
            if (!string.IsNullOrWhiteSpace(config["Smtp:User"]))
                await client.AuthenticateAsync(config["Smtp:User"]!, config["Smtp:Password"] ?? "", timeout.Token);
            sending = true;
            await client.SendAsync(message, timeout.Token);
            // A later QUIT/disconnect failure must never change an accepted result.
            return ReminderSendResult.Accepted;
        }
        catch (SmtpCommandException ex)
        {
            var status = (int)ex.StatusCode;
            return status is >= 400 and < 500 ? ReminderSendResult.Retryable
                : status >= 500 ? ReminderSendResult.Failed
                : sending ? ReminderSendResult.Uncertain : ReminderSendResult.Retryable;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or MailKit.ServiceNotConnectedException
            or MailKit.ServiceNotAuthenticatedException or SmtpProtocolException)
        { return sending ? ReminderSendResult.Uncertain : ReminderSendResult.Retryable; }
        catch
        { return sending ? ReminderSendResult.Uncertain : ReminderSendResult.Failed; }
    }
}

public static class ReminderMessage
{
    public const string SettingsUrl = "https://expenses.micutu.com/#notifications";
    public static string Text(NotificationPreviewDto preview, bool includeNames)
    {
        var text = $"Rezumat Expense Tracker — {preview.LocalDate:yyyy-MM-dd}\n\n"
            + $"În intervalul {preview.LocalDate:yyyy-MM-dd} – {preview.LocalDate.AddDays(preview.LeadDays):yyyy-MM-dd}:\n"
            + $"• {preview.RenewalCount} scadențe de abonament\n• {preview.TrialCount} perioade de probă care se încheie\n"
            + $"• {preview.GoalCount} termene ale obiectivelor de economisire\n";
        if (includeNames)
        {
            foreach (var item in preview.Items)
            {
                var kind = item.Kind == "renewal" ? "Abonament" : item.Kind == "trial" ? "Probă" : "Obiectiv";
                text += $"\n{kind}: {item.Name} — {item.Date:yyyy-MM-dd}";
            }
            text += "\n\nSunt afișate cel mult 20 de elemente.";
        }
        return text + "\n\nVerifică datele în aplicație. Acesta este un memento, nu confirmarea unei plăți sau anulări.\n"
            + "Ai activat acest rezumat. Îl poți opri din Setări cont → Notificări email:\n" + SettingsUrl;
    }
    public static MimeMessage Build(IConfiguration config, NotificationOutbox row, string recipient,
        NotificationPreviewDto preview, bool includeNames)
    {
        var message = new MimeMessage();
        var from = MailboxAddress.Parse(config["Smtp:From"] ?? config["Smtp:User"] ?? "reminders@example.invalid");
        message.From.Add(new MailboxAddress(config["Smtp:FromName"] ?? "Expense Tracker",from.Address));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.MessageId = $"reminder-{row.Id:N}@expenses.micutu.com";
        message.Date = new DateTimeOffset(row.CreatedAt);
        message.Subject = "Rezumat scadențe — Expense Tracker";
        var text = Text(preview, includeNames);
        message.Body = new BodyBuilder { TextBody = text, HtmlBody =
            "<div style=\"font-family:system-ui;max-width:600px;margin:auto;white-space:pre-wrap\">"
            + WebUtility.HtmlEncode(text) + "</div>" }.ToMessageBody();
        message.Headers.Add("Auto-Submitted", "auto-generated");
        return message;
    }
}
