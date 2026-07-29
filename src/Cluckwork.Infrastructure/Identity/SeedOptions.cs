namespace Cluckwork.Infrastructure.Identity;

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    // Opt-in. Even when true, seeding is skipped unless AdminEmail + AdminPassword
    // are supplied — there is never a hardcoded/fallback credential.
    public bool Enabled { get; init; } = true;

    public string AdminEmail { get; init; } = string.Empty;
    public string AdminPassword { get; init; } = string.Empty;
    public string AccountName { get; init; } = "Default Farm";

    // Optional second login without the Admin role (#73) — handy for testing
    // the worker experience. Same rule as the admin pair: both must be
    // supplied or the worker seed is skipped; no fallback credential exists.
    public string WorkerEmail { get; init; } = string.Empty;
    public string WorkerPassword { get; init; } = string.Empty;

    // Dev/demo only (#58): when true AND the account has no flocks, seed sample
    // flocks/entries/customers/orders through the real domain path. Default
    // false — production can never accidentally get fake data.
    public bool Demo { get; init; }

    // #243 load-test simulation gate. Config lives separately in
    // SimulationOptions (bound from its own "Simulation" section) — this flag
    // only turns the simulation seeder on/off. Default false — production
    // can never accidentally seed a load-test cast.
    public bool Simulation { get; set; }
}
