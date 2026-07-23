namespace Cluckwork.Infrastructure.Time;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Accounts;
using Microsoft.Extensions.Logging;

// Scoped: a tenant's timezone cannot change inside one request, so the account
// is read at most once and reused by every boundary check in that request —
// the stock read and the allocation in the same request cannot disagree.
//
// This FAILS CLOSED. If the timezone cannot be resolved or used, it throws
// instead of substituting UTC. Falling back would be failing open on a
// medication-safety rule: UTC is precisely the wrong answer this class exists
// to stop, so a "safe default" of UTC would release a lot restricted through
// today at 18:00 in Los Angeles — the original bug, reintroduced exactly when
// the farm is misconfigured. A misconfigured farm losing its stock screen is
// loud and fixable; selling eggs inside a withdrawal period is neither.
public sealed class FarmClock(
    IAccountRepository accounts,
    IClock clock,
    ILogger<FarmClock> logger) : IFarmClock
{
    private string? _timeZoneId;

    public async Task<DateOnly> TodayAsync(CancellationToken ct = default)
    {
        _timeZoneId ??= await ResolveTimeZoneIdAsync(ct);

        try
        {
            return clock.TodayInZone(_timeZoneId);
        }
        // ArgumentException covers ArgumentNullException: FindSystemTimeZoneById
        // throws those — not TimeZoneNotFoundException — for a null or blank id.
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            logger.LogError(
                ex, "Account timezone '{TimeZoneId}' is not usable; refusing to fall back to UTC.", _timeZoneId);
            throw new FarmTimeZoneException(
                $"The farm's timezone ('{_timeZoneId}') is not usable, so no date-dependent operation can be trusted.", ex);
        }
    }

    private async Task<string> ResolveTimeZoneIdAsync(CancellationToken ct)
    {
        var account = await accounts.GetCurrentAsync(ct);

        // Unreachable on the request paths that use this (they check the tenant
        // is resolved first), so it means something is wired wrong rather than
        // "this farm is on UTC".
        if (account is null)
            throw new FarmTimeZoneException("No account is resolved, so the farm's date boundary is unknown.");

        if (string.IsNullOrWhiteSpace(account.TimeZoneId))
        {
            logger.LogError("Account {AccountId} has no timezone set.", account.Id);
            throw new FarmTimeZoneException($"Account {account.Id} has no timezone set.");
        }

        return account.TimeZoneId;
    }
}

// Distinct type so the failure is greppable in logs and can be mapped to a
// configuration problem rather than read as a generic 500.
public sealed class FarmTimeZoneException : InvalidOperationException
{
    public FarmTimeZoneException(string message) : base(message) { }
    public FarmTimeZoneException(string message, Exception inner) : base(message, inner) { }
}
