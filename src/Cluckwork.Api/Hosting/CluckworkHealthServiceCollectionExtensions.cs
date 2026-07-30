namespace Cluckwork.Api.Hosting;

internal static class CluckworkHealthServiceCollectionExtensions
{
    public static IServiceCollection AddCluckworkHealthChecks(
        this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<Cluckwork.Api.HealthChecks.DatabaseReadyHealthCheck>(
                "database")
            .AddCheck<Cluckwork.Api.HealthChecks.DurableJobWorkerHealthCheck>(
                "durable-job-worker");

        return services;
    }
}
