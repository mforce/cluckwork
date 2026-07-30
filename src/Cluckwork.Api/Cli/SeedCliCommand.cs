namespace Cluckwork.Api.Cli;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// `seed --profile <name>` (#280) — a one-off command on the same binary, not a
// serving-process code path: it migrates the schema, runs the requested
// profile's seeder(s), then EXITS (Kestrel and the hosted services never start).
public sealed class SeedCliCommand : ICliCommand
{
    public string Name => "seed";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        using var seedScope = app.Services.CreateScope();
        var sp = seedScope.ServiceProvider;
        var profile = CliDispatcher.ArgValue(args, "--profile");

        // #284 review — validate the profile AND its availability in this
        // environment (the DI-registration/prod-guard check) BEFORE touching the
        // database at all. Previously MigrateAsync ran first, so an unknown
        // profile or a Production-blocked "demo" still mutated the schema (or
        // threw raw) before the guard below ever ran. Nothing under this switch
        // may write to the database — it only resolves services and picks which
        // seed delegate to run once validation has passed.
        Func<Task<SeedResult>>? runSeed;
        switch (profile)
        {
            case "demo":
            {
                // DemoDataSeeder is registered only outside Production (see the DI
                // registration in Program.cs) — GetService (not GetRequiredService)
                // turns a missing registration into a clear operator-facing message
                // instead of an opaque DI resolution exception.
                var demoSeeder = sp.GetService<DemoDataSeeder>();
                if (demoSeeder is null)
                {
                    await Console.Error.WriteLineAsync(
                        "Demo seeding is not available in Production (DemoDataSeeder is not registered).");
                    return 1;
                }
                runSeed = () => demoSeeder.SeedAsync();
                break;
            }
            case "simulation":
            {
                // SimulationDataSeeder is registered only outside Production — same
                // GetService guard as demo.
                var simSeeder = sp.GetService<SimulationDataSeeder>();
                if (simSeeder is null)
                {
                    await Console.Error.WriteLineAsync(
                        "Simulation seeding is not available in Production (SimulationDataSeeder is not registered).");
                    return 1;
                }
                runSeed = () => simSeeder.SeedAsync();
                break;
            }
            default:
                await Console.Error.WriteLineAsync(
                    $"Unknown or missing --profile '{profile}'. Known: demo, simulation.");
                return 1;
        }

        await sp.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        // Fail-loud (#284 review): SeedAsync reports what happened instead of
        // swallowing a no-op or an internal failure into a silent exit 0. Only
        // Seeded/AlreadySeeded (the seeder's own idempotency guard) are success —
        // everything else is a clear stderr message + non-zero exit.
        var result = await runSeed();
        if (!result.IsSuccess)
        {
            await Console.Error.WriteLineAsync(result.Message);
            return 1;
        }

        app.Logger.LogInformation(
            "Seed command complete (profile={Profile}): {Message}", profile, result.Message);
        return 0;
    }
}
