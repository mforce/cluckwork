namespace Cluckwork.Application.Features.Accounts;

using System.Globalization;

public static class FarmSettingsRules
{
    public static bool IsKnownTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return false;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            return true;
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
