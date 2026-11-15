using System.Globalization;
using System.Text;
using ExpenseTracker.Api.Models;

namespace ExpenseTracker.Api.Infrastructure;

public static class CalendarExport
{
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r\n", "\\n")
        .Replace("\r", "\\n").Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");

    // RFC 5545: content lines fold at 75 octets without splitting UTF-8 characters.
    private static void Line(StringBuilder output, string value)
    {
        var octets = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (octets + rune.Utf8SequenceLength > 75) { output.Append("\r\n "); octets = 1; }
            output.Append(rune.ToString()); octets += rune.Utf8SequenceLength;
        }
        output.Append("\r\n");
    }

    public static byte[] Build(IEnumerable<SubscriptionItem> subscriptions, DateTime utcNow)
    {
        var output = new StringBuilder();
        Line(output, "BEGIN:VCALENDAR"); Line(output, "VERSION:2.0");
        Line(output, "PRODID:-//Expense Tracker//Subscriptions//RO"); Line(output, "CALSCALE:GREGORIAN");
        foreach (var item in subscriptions.Where(s => s.IsActive && s.NextBillingDate != default && s.NextBillingDate.Year < 9999))
        {
            Line(output, "BEGIN:VEVENT"); Line(output, $"UID:{item.Id:D}@expense-tracker");
            Line(output, "DTSTAMP:" + utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            Line(output, "DTSTART;VALUE=DATE:" + item.NextBillingDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Line(output, "DTEND;VALUE=DATE:" + item.NextBillingDate.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Line(output, "SUMMARY:" + Escape(item.Name));
            Line(output, "DESCRIPTION:" + Escape($"Cost planificat: {item.Cost.ToString("0.00", CultureInfo.InvariantCulture)} {item.Currency}. Nu confirmă efectuarea plății."));
            Line(output, "TRANSP:TRANSPARENT"); Line(output, "END:VEVENT");
        }
        Line(output, "END:VCALENDAR");
        return Encoding.UTF8.GetBytes(output.ToString());
    }
}
