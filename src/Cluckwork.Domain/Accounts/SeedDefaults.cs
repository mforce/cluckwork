namespace Cluckwork.Domain.Accounts;

// Stable well-known ids for the single-farm MVP seed. Farm and House have no
// aggregates of their own yet (Flock/DailyEntry reference them by id), so these
// constants stand in as the default farm/house until those entities exist.
public static class SeedDefaults
{
    public static readonly Guid AccountId = new("0000000a-0000-0000-0000-000000000001");
    public static readonly Guid FarmId = new("0000000f-0000-0000-0000-000000000001");
    public static readonly Guid HouseId = new("0000000b-0000-0000-0000-000000000001");
}
