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

    // The fallback provisioning timezone — a single source shared by the property
    // default below AND the Program.cs boot guard's `?? DefaultTimeZoneId`, so the
    // guard always validates exactly the value the seeder would use even if this
    // default changes (#264 review — the two must never diverge).
    public const string DefaultTimeZoneId = "UTC";

    // #264 — the IANA timezone the default account is provisioned with. The farm
    // clock (daily-entry boundary, 7-day auto-lock, FIFO availability) runs on
    // this and FAILS CLOSED on an unusable zone, so provisioning a real farm on
    // the literal "UTC" default silently ran every safety boundary in the wrong
    // zone. Set this to the farm's real IANA id (e.g. "Asia/Manila") at cutover.
    // Validated at boot (Program.cs) so a typo fails loud immediately instead of
    // surfacing later as a per-request FarmTimeZoneException. NOTE: this only
    // provisions a NEW account — changing it after first boot does nothing (the
    // seeder skips an existing account); use Settings → timezone to re-time a
    // live farm.
    public string TimeZoneId { get; init; } = DefaultTimeZoneId;

    // Optional second login without the Admin role (#73) — handy for testing
    // the worker experience. Same rule as the admin pair: both must be
    // supplied or the worker seed is skipped; no fallback credential exists.
    public string WorkerEmail { get; init; } = string.Empty;
    public string WorkerPassword { get; init; } = string.Empty;

    // Dev/demo only (#58): when true AND the account has no flocks, seed sample
    // flocks/entries/customers/orders through the real domain path. Default
    // false — production can never accidentally get fake data.
    public bool Demo { get; init; }

    // #243/#279: there is deliberately NO Simulation gate here. The simulation
    // seeder is invoked ONLY by the explicit `seed --profile simulation`
    // command (Program.cs), which is itself the gate (plus the Production
    // DI-registration guard there) — same shape as demo. Its data-shape config
    // lives separately in SimulationOptions (bound from its own "Simulation"
    // section).
}
