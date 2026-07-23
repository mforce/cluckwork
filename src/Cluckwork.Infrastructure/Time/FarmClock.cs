namespace Cluckwork.Infrastructure.Time;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Microsoft.Extensions.Logging;

// Scoped: a tenant's timezone cannot change inside one request, so the account
// is read at most once and reused by every boundary check in that request —
// the stock read and the allocation in the same request cannot disagree.
public sealed class FarmClock(
    IAccountRepository accounts,
    IClock clock,
    ILogger<FarmClock> logger) : IFarmClock
{
    private string? _timeZoneId;

    public async Task<DateOnly> TodayAsync(CancellationToken ct = default)
    {
        // No account (unresolved tenant) keeps the previous UTC behaviour rather
        // than throwing on a path that used to work.
        _timeZoneId ??= (await accounts.GetCurrentAsync(ct))?.TimeZoneId ?? "UTC";

        try
        {
            return clock.TodayInZone(_timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // An unusable timezone id must not take the farm offline: every
            // boundary check runs on the hot path, so a throw here would 500
            // the stock screen and every sale. Fall back to the old UTC
            // boundary and make the misconfiguration loud in the logs instead.
            logger.LogError(
                ex, "Account timezone '{TimeZoneId}' is not usable; falling back to the UTC date boundary.",
                _timeZoneId);
            return clock.TodayUtc;
        }
    }
}
