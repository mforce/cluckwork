namespace Cluckwork.Infrastructure.Providers.Postgres;

using Microsoft.EntityFrameworkCore;

public sealed class PostgresDbContextConfigurator : IDbProviderConfigurator
{
    // Migrations live here so provider-specific SQL stays separate (tech spec §5.2).
    public string MigrationsAssembly => typeof(PostgresDbContextConfigurator).Assembly.GetName().Name!;

    public void Configure(
        DbContextOptionsBuilder builder, string connectionString, DatabaseResilienceOptions resilience)
    {
        // connectionString is expected to be already normalized (URI -> key-value) and
        // TLS-validated by PostgresConnectionString.NormalizeAndValidate at startup
        // (#261/#262) — done ONCE there, not per DbContext resolution.
        builder.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.MigrationsAssembly(MigrationsAssembly);

            // #269 — a managed-Postgres failover or a dropped pooled connection
            // must retry instead of throwing straight to a 500 on the request
            // path. NpgsqlRetryingExecutionStrategy only retries what Npgsql
            // itself classifies transient (NpgsqlException.IsTransient /
            // PostgresException.IsTransient — connection loss, "cannot connect
            // now", admin/crash shutdown, serialization failure, etc.); a
            // constraint violation or any other non-transient failure still
            // propagates on the first attempt.
            //
            // This forces every EXPLICIT user-initiated transaction elsewhere
            // in the app through database.CreateExecutionStrategy().ExecuteAsync
            // — EF Core throws InvalidOperationException the moment anything
            // touches the DbContext inside a manually-begun transaction that
            // was opened outside an execution strategy, retryable failure or
            // not. See AmbientTransaction (used by UnitOfWork and
            // IdentityProvider), IdempotencyMiddleware's own request-wide
            // transaction, ExportQueries.BeginConsistentReadAsync, and
            // DemoDataSeeder.CleanupPartialSeedAsync — every one of those was
            // updated for this (#269). Being inside a strategy is NOT the same
            // as being retried: all but the last of those run through
            // SingleAttemptExecution, which executes exactly once because the
            // work they wrap is not replayable. Read it before widening this.
            npgsql.EnableRetryOnFailure(
                resilience.MaxRetryCount,
                TimeSpan.FromSeconds(resilience.MaxRetryDelaySeconds),
                errorCodesToAdd: null);
        });
    }
}
