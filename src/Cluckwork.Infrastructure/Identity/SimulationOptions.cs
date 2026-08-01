namespace Cluckwork.Infrastructure.Identity;

/// Config for the #243 load-test simulation seeder (bound from the "Simulation"
/// section). #283: the Owner persona reuses whichever Owner the `bootstrap-admin`
/// verb already provisioned — SeedOptions and the `Seed:AdminEmail`/`Seed:AdminPassword`
/// config keys are retired along with the runtime seeder that fed them, so no
/// admin credential is configured here or anywhere else. The counts below are
/// the ADDITIONAL cast beyond that Owner.
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
