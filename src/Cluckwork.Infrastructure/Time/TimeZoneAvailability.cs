namespace Cluckwork.Infrastructure.Time;

// #264 — Cluckwork's farm-clock date logic (the daily-entry "today" boundary,
// the 7-day auto-lock sweep, FIFO lot availability) resolves IANA zones via
// TimeZoneInfo.FindSystemTimeZoneById, which needs the runtime image to carry
// the tz database + ICU. The Debian `aspnet:10.0` base ships both, but nothing
// pinned or tested that: switching to an Alpine/chiseled image (no tzdata) or
// setting InvariantGlobalization=true would throw for EVERY farm's date logic
// at once — a fleet-wide outage that FarmClock's fail-closed design would
// otherwise surface only as a per-request FarmTimeZoneException at runtime.
//
// This turns that latent failure into a loud, immediate boot/config failure
// with an actionable message. Called eagerly at startup (Program.cs) as an image
// canary, and against the configured Seed:TimeZoneId so a provisioning typo
// fails at boot rather than at the first stock screen.
public static class TimeZoneAvailability
{
    // A representative DST zone used purely as the image canary: resolving it
    // proves the FULL tz database is present, not just fixed-offset fallbacks.
    // It is NOT any farm's configured zone.
    public const string CanaryZoneId = "America/New_York";

    // Throws InvalidOperationException with an operator-actionable message when
    // the runtime cannot resolve the given IANA id; returns normally otherwise.
    public static void EnsureResolvable(string timeZoneId, string context)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        // FindSystemTimeZoneById throws ArgumentException (covers
        // ArgumentNullException) for a null/blank id, TimeZoneNotFoundException
        // when the id is unknown / tzdata is absent, and InvalidTimeZoneException
        // for corrupt data — mirror FarmClock's catch set.
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"{context}: the runtime cannot resolve IANA time zone '{timeZoneId}'. " +
                "Cluckwork's farm-clock date logic requires the image to contain tz data (tzdata/ICU) " +
                "and to NOT use InvariantGlobalization. Use a base image that ships the tz database " +
                "(e.g. the Debian mcr.microsoft.com/dotnet/aspnet:10.0 image).", ex);
        }
    }
}
