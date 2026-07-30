namespace Cluckwork.Infrastructure.Persistence;

// Outcome of a seed-profile SeedAsync() call (#284 review; #279 moved it out of
// DemoDataSeeder.cs into its own file now that it is shared by BOTH
// DemoDataSeeder and SimulationDataSeeder). The only callers are the `seed
// --profile <name>` CLI command's cases (Program.cs) — each must be able to
// tell "actually seeded", "already present" (idempotent no-op), and "did not
// seed" apart, and get an operator-facing message for the last two, instead of
// a bare bool/void that reads as success either way.
public enum SeedStatus
{
    Seeded,
    AlreadySeeded,
    PrerequisitesMissing,
    Failed
}

public sealed record SeedResult(SeedStatus Status, string Message)
{
    // Seeded and AlreadySeeded are both "the profile's data is present"
    // outcomes — the command exits 0 for either. PrerequisitesMissing/Failed
    // exit non-zero.
    public bool IsSuccess => Status is SeedStatus.Seeded or SeedStatus.AlreadySeeded;

    public static SeedResult Seeded(string message) => new(SeedStatus.Seeded, message);
    public static SeedResult AlreadySeeded(string message) => new(SeedStatus.AlreadySeeded, message);
    public static SeedResult PrerequisitesMissing(string message) => new(SeedStatus.PrerequisitesMissing, message);
    public static SeedResult Failed(string message) => new(SeedStatus.Failed, message);
}
