using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Services;

/// <summary>
/// Sends transactional email (verification / password-reset codes) over SMTP,
/// configured from Smtp:* settings (mailcow in production). When no SMTP host is
/// configured the code is logged at Warning level instead, so local dev works
/// without a mail server.
/// </summary>
public class EmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config["Smtp:Host"]);

    public async Task SendCodeAsync(string toEmail, string code, VerificationPurpose purpose)
    {
        var (subject, intro) = purpose switch
        {
            VerificationPurpose.PasswordReset =>
                ("Cod resetare parolă — Expense Tracker",
                 "Ai cerut resetarea parolei. Folosește codul de mai jos pentru a continua."),
            _ =>
                ("Cod verificare cont — Expense Tracker",
                 "Bine ai venit! Confirmă-ți adresa de email folosind codul de mai jos."),
        };

        if (!IsConfigured)
        {
            _logger.LogWarning(
                "SMTP not configured — {Purpose} code for {Email} is {Code} (dev fallback)",
                purpose, toEmail, code);
            return;
        }

        var fromAddress = _config["Smtp:From"] ?? _config["Smtp:User"]!;
        var fromName = _config["Smtp:FromName"] ?? "Expense Tracker";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder
        {
            HtmlBody = BuildHtml(intro, code),
            TextBody = $"{intro}\n\nCod: {code}\n\nCodul expiră în 15 minute."
        }.ToMessageBody();

        var host = _config["Smtp:Host"]!;
        var port = _config.GetValue<int?>("Smtp:Port") ?? 587;
        var useStartTls = _config.GetValue<bool?>("Smtp:UseStartTls") ?? true;
        var socketOption = useStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;

        using var client = new SmtpClient();
        await client.ConnectAsync(host, port, socketOption);

        var user = _config["Smtp:User"];
        var pass = _config["Smtp:Password"];
        if (!string.IsNullOrWhiteSpace(user))
            await client.AuthenticateAsync(user, pass ?? string.Empty);

        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    private static string BuildHtml(string intro, string code) => $"""
        <div style="font-family:system-ui,Segoe UI,Arial,sans-serif;max-width:480px;margin:0 auto;padding:24px;color:#111">
          <h2 style="margin:0 0 8px">Expense Tracker</h2>
          <p style="color:#555;font-size:14px">{intro}</p>
          <div style="font-size:34px;font-weight:800;letter-spacing:8px;background:#f4f4f5;border-radius:12px;padding:18px;text-align:center;margin:18px 0">{code}</div>
          <p style="color:#888;font-size:12px">Codul expiră în 15 minute. Dacă nu ai cerut tu acest cod, ignoră acest email.</p>
        </div>
        """;
}
