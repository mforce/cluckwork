namespace Cluckwork.Api.Cli;

using Cluckwork.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// `seed --profile <name> [--farm-code <slug>]` (#280) — a one-off command on the
// same binary, not a serving-process code path: it migrates the schema, runs the
// requested profile's seeder(s), then EXITS (Kestrel and the hosted services
// never start).
//
// `--farm-code` points the DEMO profile at a farm other than the default one,
// resolved by slug exactly as the account lifecycle verbs resolve theirs. It
// exists because the README's dashboard screenshot cannot be captured from the
// simulation fixture: that fixture seeds 100 catalog flocks which never file, so
// every day is a partial day and the trend strip has no complete day to scale
// against. The sim harness therefore provisions a second, small farm and seeds
// it with the demo profile (tools/simulation/reset.sh).
//
// `--profile simulation` deliberately does NOT accept it. SimulationDataSeeder
// writes the default account throughout — its manifest, its cast emails and the
// counts k6 and the e2e suite pin are all default-farm facts — so honouring the
// flag there would need a second decision, not a parameter. It exits 1 instead
// of silently seeding the wrong farm.
public sealed class SeedCliCommand : ICliCommand
{
    public string Name => "seed";

    public async Task<int> RunAsync(WebApplication app, string[] args)
    {
        using var seedScope = app.Services.CreateScope();
        var sp = seedScope.ServiceProvider;
        var profile = CliDispatcher.ArgValue(args, "--profile");
        // Folded like every other verb's --slug: an operator typing README-FARM at
        // a shell means readme-farm. Null when the flag is absent OR blank, which
        // is what makes "absent" and "--farm-code ''" behave the same way.
        var farmCode = AccountSlugLookup.Normalize(CliDispatcher.ArgValue(args, "--farm-code"));

        // #284 review — validate the profile AND its availability in this
        // environment (the DI-registration/prod-guard check) BEFORE touching the
        // database at all. Previously MigrateAsync ran first, so an unknown
        // profile or a Production-blocked "demo" still mutated the schema (or
        // threw raw) before the guard below ever ran. Nothing under this switch
        // may write to the database — it only resolves services and picks which
        // seed delegate to run once validation has passed. The slug LOOKUP is
        // deliberately not here either: it is a read, but against a schema this
        // command's own MigrateAsync may not have created yet.
        Func<Guid?, Task<SeedResult>>? runSeed;
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
                    await WriteErrorAsync(
                        "Demo seeding is not available in Production (DemoDataSeeder is not registered).");
                    return 1;
                }
                runSeed = accountId => demoSeeder.SeedAsync(accountId);
                break;
            }
            case "simulation":
            {
                if (farmCode is not null)
                {
                    await WriteErrorAsync(
                        "seed --profile simulation does not accept --farm-code: the simulation fixture is " +
                        "default-farm-only by design (its manifest, its cast emails and the counts k6 and " +
                        "the e2e suite pin are all facts about that farm). Use --profile demo to seed " +
                        "another farm.");
                    return 1;
                }

                // SimulationDataSeeder is registered only outside Production — same
                // GetService guard as demo.
                var simSeeder = sp.GetService<SimulationDataSeeder>();
                if (simSeeder is null)
                {
                    await WriteErrorAsync(
                        "Simulation seeding is not available in Production (SimulationDataSeeder is not registered).");
                    return 1;
                }
                runSeed = _ => simSeeder.SeedAsync();
                break;
            }
            default:
                await WriteErrorAsync(
                    $"Unknown or missing --profile '{profile}'. Known: demo, simulation.");
                return 1;
        }

        await sp.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        // Resolved AFTER the migrate, because the Accounts table may be something
        // this command has only just created; resolved BEFORE the seed, so an
        // unknown code costs nothing. A null here means "the default farm" — the
        // seeder's own default — and never "resolution failed", which returns.
        Guid? targetAccountId = null;
        if (farmCode is not null)
        {
            targetAccountId = await AccountSlugLookup.ResolveAsync(sp, farmCode);
            if (targetAccountId is null)
            {
                await WriteErrorAsync(
                    $"No farm with code '{farmCode}'. Run `list-accounts` to see the codes this database " +
                    "holds, or `provision-account` to create that farm first.");
                return 1;
            }
        }

        // Fail-loud (#284 review): SeedAsync reports what happened instead of
        // swallowing a no-op or an internal failure into a silent exit 0. Only
        // Seeded/AlreadySeeded (the seeder's own idempotency guard) are success —
        // everything else is a clear stderr message + non-zero exit.
        var result = await runSeed(targetAccountId);
        if (!result.IsSuccess)
        {
            await WriteErrorAsync(result.Message);
            return 1;
        }

        app.Logger.LogInformation(
            "Seed command complete (profile={Profile}, farm={FarmCode}): {Message}",
            profile, farmCode ?? "default", result.Message);
        return 0;
    }

    // EVERY stderr path in this verb routes through here, which is where the
    // #560 control-character strip lives. Two of the messages quote raw argv —
    // the unresolved --farm-code and the unknown --profile — and a value carrying
    // a newline would otherwise let an operator's terminal read a second, forged
    // line as this command's own output. Routing only those two is the shape a
    // per-line fix produces and the shape rename-account was corrected out of
    // (#732 round 1): the messages that are already safe pay nothing, and the
    // next message added here cannot be the one that was missed.
    private static Task WriteErrorAsync(string message) =>
        Console.Error.WriteLineAsync(ListAccountsCliCommand.SanitizeForDisplay(message));
}
