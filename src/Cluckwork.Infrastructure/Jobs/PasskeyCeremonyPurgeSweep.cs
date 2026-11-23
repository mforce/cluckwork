using Cluckwork.Application.Modules.Access.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cluckwork.Infrastructure.Jobs;

// #1282 decision 3: deletes passkey ceremonies past their ExpiresAt from the
// DurableJobWorker poll, in bounded batches, as RefreshTokenPurgeSweep does.
// No grace: an expired ceremony is refused at its finish anyway. No tenant
// loop: the table has no AccountId.
public sealed class PasskeyCeremonyPurgeSweep(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PasskeyCeremonyPurgeSweep> logger)
{
    private const int BatchSize = 500;
    private const int MaxBatchesPerRun = 20;

    public async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var purge = scope.ServiceProvider.GetRequiredService<IPasskeyCeremonyPurge>();
        var cutoff = timeProvider.GetUtcNow();
        var total = 0;

        try
        {
            for (var batch = 0; batch < MaxBatchesPerRun; batch++)
            {
                var deleted = await purge.DeleteExpiredBatchAsync(cutoff, BatchSize, ct);
                total += deleted;
                if (deleted < BatchSize) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Housekeeping, contained as the other sweeps are.
            logger.LogError(ex, "Passkey ceremony purge sweep failed; will retry next poll.");
            return;
        }

        if (total > 0)
            logger.LogInformation("Purged {Count} expired passkey ceremonies.", total);
    }
}
