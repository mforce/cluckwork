using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Infrastructure.Jobs;

// #797 — clears dead OAuth rows from the DurableJobWorker poll, under its leader gate
// (#271), so one replica runs it. OpenIddict's own Quartz job would be a second
// scheduler outside that gate. No tenant: the OAuth tables carry no AccountId (#795).
//
// IOAuthPurge exists only where the OAuth server is registered (#795), so in Production
// until #798 this does nothing.
public sealed class OAuthPurgeSweep(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OAuthPurgeSweep> logger)
{
    // OpenIddict's default. A dead row is kept this long after creation so a replayed
    // code or revoked token still reads as such, rather than as unknown.
    public static readonly TimeSpan PruneRetention = TimeSpan.FromDays(14);

    // A client registers just before it sends the user to approve it, so a day is far
    // longer than any real approval takes, and short enough that junk registrations
    // stay at roughly one day of the registration rate limit.
    public static readonly TimeSpan UnapprovedWindow = TimeSpan.FromDays(1);

    public async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var purge = scope.ServiceProvider.GetService<IOAuthPurge>();
        if (purge is null)
            return;

        OAuthPurgeResult result;
        try
        {
            result = await purge.PurgeAsync(timeProvider.GetUtcNow() - PruneRetention, UnapprovedWindow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Housekeeping, contained like the sibling sweeps so a failure here does not
            // push the worker into backoff and delay the daily-entry lock sweep.
            logger.LogError(ex, "OAuth purge sweep failed; will retry next poll.");
            return;
        }

        if (result is not { Tokens: 0, Authorizations: 0, Applications: 0 })
            logger.LogInformation(
                "Purged {Tokens} OAuth tokens, {Authorizations} authorizations and {Applications} unapproved applications.",
                result.Tokens, result.Authorizations, result.Applications);
    }
}
