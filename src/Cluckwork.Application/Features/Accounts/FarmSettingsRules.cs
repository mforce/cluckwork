namespace Cluckwork.Application.Features.Accounts;

using System.Globalization;

public static class FarmSettingsRules
{
    public static bool IsKnownTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return false;
        try
        {
            var normalized = timeZoneId.Trim();
            _ = TimeZoneInfo.FindSystemTimeZoneById(normalized);
            return normalized.Equals("UTC", StringComparison.Ordinal)
                || TimeZoneInfo.TryConvertIanaIdToWindowsId(normalized, out _);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException
                                      or InvalidTimeZoneException
                                      or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsSpecificCulture(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return false;
        try
        {
            var culture = CultureInfo.GetCultureInfo(locale.Trim(), predefinedOnly: true);
            return !culture.IsNeutralCulture && culture.LCID != CultureInfo.InvariantCulture.LCID;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
