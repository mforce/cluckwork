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

    // Dev/demo only (#58): when true AND the account has no flocks, seed sample
    // flocks/entries/customers/orders through the real domain path. Default
    // false — production can never accidentally get fake data.
    public bool Demo { get; init; }
}
