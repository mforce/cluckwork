namespace Cluckwork.Infrastructure.Time;

using Cluckwork.Application.Common;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public DateOnly TodayUtc => DateOnly.FromDateTime(DateTime.UtcNow);

    public DateOnly TodayInZone(string timeZoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
    }
}
