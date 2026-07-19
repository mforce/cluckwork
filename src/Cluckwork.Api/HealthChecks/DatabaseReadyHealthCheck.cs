namespace Cluckwork.Api.HealthChecks;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// Readiness = the database is reachable AND the schema is current (tech spec:
// "DB connectivity, migrations applied"). A connectivity-only check would
// report healthy against a stale schema when Database:MigrateOnStartup=false
// and the deploy job hasn't run yet (codex review of PR #79).
public sealed class DatabaseReadyHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            return pending.Count == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"{pending.Count} database migration(s) pending.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database is unreachable.", ex);
        }
    }
}
