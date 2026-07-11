namespace Cluckwork.Application.Common;

public interface IClock
{
    DateTime UtcNow { get; }
    DateOnly TodayUtc { get; }
    DateOnly TodayInZone(string timeZoneId);
}
