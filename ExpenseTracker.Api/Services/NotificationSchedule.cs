namespace ExpenseTracker.Api.Services;

public static class NotificationSchedule
{
    public static readonly IReadOnlyList<string> Zones = TimeZoneInfo.GetSystemTimeZones()
        .Select(z => z.Id).Append("UTC").Where(id => id == "UTC" || id.Contains('/')).Distinct().Order().ToArray();
    private static readonly HashSet<string> KnownZones = Zones.ToHashSet(StringComparer.Ordinal);
    public static TimeZoneInfo? Zone(string? id)
    {
        if (id is null || !KnownZones.Contains(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }
    public static DateTime AtLocalHour(DateOnly date, int hour, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Unspecified);
        // DST can skip an hour, or (for a few zones) an entire civil date.
        for (var i = 0; zone.IsInvalidTime(local) && i < 1441; i++) local = local.AddMinutes(1);
        if (zone.IsInvalidTime(local)) throw new InvalidTimeZoneException("No valid notification time.");
        return zone.IsAmbiguousTime(local)
            ? DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc)
            : TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
    public static DateTime Next(DateTime now, TimeZoneInfo zone, int hour)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
        var at = AtLocalHour(date, hour, zone);
        return at > now ? at : AtLocalHour(date.AddDays(1), hour, zone);
    }
}
