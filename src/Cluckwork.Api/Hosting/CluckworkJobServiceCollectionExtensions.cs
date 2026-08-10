namespace Cluckwork.Api.Hosting;

using Cluckwork.Infrastructure.Jobs;

internal static class CluckworkJobServiceCollectionExtensions
{
    public static IServiceCollection AddCluckworkJobs(
        this IServiceCollection services)
    {
        services.AddSingleton<DurableJobWorkerHeartbeat>();
        services.AddSingleton<DailyEntryLockSweep>();
        services.AddSingleton<RefreshTokenPurgeSweep>();
        services.AddSingleton<IdempotencyRecordPurgeSweep>();
        services.AddHostedService<DurableJobWorker>();
        return services;
    }
}
