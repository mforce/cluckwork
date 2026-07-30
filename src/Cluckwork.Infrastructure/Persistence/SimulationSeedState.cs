namespace Cluckwork.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

// #279 review (codex re-check) — a durable bookmark for the simulation seed, so
// idempotency never depends on INFERRING state from the fixture rows themselves.
// Exactly one row per seeded account (AccountId is the PK):
//   - Anchor: the "today" captured on the FIRST run, written BEFORE any dated
//     fixture row and reused verbatim on every re-run, so every date-relative
//     natural key re-derives identically. This survives a UTC-day rollover,
//     foreign daily entries a load test writes into the account (which would
//     otherwise poison a max(entry date) recovery), AND a crash before the first
//     entry is written (a data-derived anchor would be absent there).
//   - CompletedAtUtc: set ONLY after exact validation + manifest emission
//     succeed — the real "a prior run finished" signal. Null => not yet complete,
//     so a run interrupted after writing fixtures but before the manifest is
//     (correctly) reported as Seeded, never AlreadySeeded, on the next attempt.
// NOT tenant-filtered (mirrors IdempotencyRecord): it is seeder bookkeeping keyed
// explicitly by AccountId, read before/around tenant resolution.
public sealed class SimulationSeedState
{
    public Guid AccountId { get; set; }
    public DateOnly Anchor { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public static class SimulationSeedStateModelBuilderExtensions
{
    public static void ConfigureSimulationSeedState(this ModelBuilder builder)
    {
        builder.Entity<SimulationSeedState>(entity =>
        {
            entity.ToTable("simulation_seed_state");
            entity.HasKey(e => e.AccountId);
            entity.Property(e => e.Anchor).IsRequired();
        });
    }
}
