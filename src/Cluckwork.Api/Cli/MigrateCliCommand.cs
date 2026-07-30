namespace Cluckwork.Api.Cli;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// `migrate` (#263) — applies EF migrations then EXITS: the pre-deploy-job
// entrypoint that lets a production deploy run schema DDL under a dedicated
// migrator/owner credential (a one-off job), with `Database:MigrateOnStartup=false`
// so the request-serving process — running under a least-privilege runtime role
// with no DDL grant — never applies DDL at request time (the #263 goal). Same
// run-then-exit shape as `seed`; fail-loud (non-zero exit + stderr on failure).
public sealed class MigrateCliCommand : ICliCommand
{
    public string Name => "migrate";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        try
        {
            // Inside the try so a DI/provider resolution failure (e.g. a misspelled
            // Database__Provider) also fails loud with exit 1 + a clean message,
            // rather than an unhandled stack trace (#263 review).
            using var migrateScope = app.Services.CreateScope();
            var db = migrateScope.ServiceProvider.GetRequiredService<AppDbContext>();

            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            if (pending.Count == 0)
            {
                app.Logger.LogInformation("Migrate: schema already current; no migrations to apply.");
                return 0;
            }
            app.Logger.LogInformation(
                "Migrate: applying {Count} pending migration(s): {Migrations}",
                pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync();
            app.Logger.LogInformation("Migrate: schema is now current.");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Migrate failed: {ex.Message}");
            app.Logger.LogError(ex, "Migrate command failed.");
            return 1;
        }
    }
}
