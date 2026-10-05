namespace UncoveringGreatnessCRM.Helpers;

/// <summary>
/// The business operates in South Africa (SAST, UTC+2, no DST). Timestamps of system events are stored in UTC;
/// user-entered schedule fields (follow-ups, due dates, calendar entries) are stored as local business time.
/// </summary>
public static class Clock
{
    private static readonly TimeZoneInfo Zone = ResolveZone();

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Africa/Johannesburg", "South Africa Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* try next */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("SAST", TimeSpan.FromHours(2), "SAST", "SAST");
    }

    public static DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);
    public static DateTime LocalToday => LocalNow.Date;

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
}
