namespace Cluckwork.Api.Hosting;

using Cluckwork.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;

internal static class CluckworkJobServiceCollectionExtensions
{
    public static IServiceCollection AddCluckworkJobs(
        this IServiceCollection services)
    {
        services.AddSingleton<DurableJobWorkerHeartbeat>();
        services.AddSingleton<DailyEntryLockSweep>();
        services.AddSingleton<RefreshTokenPurgeSweep>();
        services.AddSingleton<IdempotencyRecordPurgeSweep>();
        // #271 — the single-runner gate the worker acquires before polling.
        services.AddSingleton<ILeaderLease>(sp => new PostgresLeaderLease(
            sp.GetRequiredService<LeaderLeaseConnectionString>().Value,
            sp.GetRequiredService<ILogger<PostgresLeaderLease>>()));
        services.AddHostedService<DurableJobWorker>();
        return services;
    }
}
