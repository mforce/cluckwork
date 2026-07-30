namespace Cluckwork.Infrastructure.Identity;

/// Config for the #243 load-test simulation seeder (bound from the "Simulation"
/// section, separate from SeedOptions). The Owner persona reuses the seeded
/// admin (Seed:AdminEmail/Password); the counts below are the ADDITIONAL cast.
public sealed class SimulationOptions
{
    public const string SectionName = "Simulation";
    public int HistoryDays { get; set; } = 90;
    public int Managers { get; set; } = 1;
    public int Sales { get; set; } = 1;
    public int Workers { get; set; } = 3;
    public int ReadOnly { get; set; } = 4;
    public string TimeZoneId { get; set; } = "America/New_York";
    public string EmailDomain { get; set; } = "sim.local";
    /// Shared, runtime-generated password for the whole sim cast (never hardcoded;
    /// bootstrap generates it into .env.sim). Required when Simulation is enabled.
    public string CastPassword { get; set; } = "";
    public int Seed { get; set; } = 243;
    /// Optional container path SimulationDataSeeder writes its completion
    /// manifest to (#243 Task 3e — row counts, lifecycle-state matrix,
    /// complete/fingerprint; see SimulationDataSeeder.EmitManifestAsync).
    /// When null, the seeder skips the FILE write but still runs the count
    /// validation (keeps integration tests free of a volume mount). Despite
    /// the name, this is NOT the cast credential file — that's a separate
    /// artifact bootstrap owns (a later #243 task); the property name is kept
    /// to avoid churn on Task 1's binding test.
    public string? CredentialOutputPath { get; set; }
}
