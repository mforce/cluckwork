namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Eggs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class DailyEntryConfiguration : IEntityTypeConfiguration<DailyEntry>
{
    public void Configure(EntityTypeBuilder<DailyEntry> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.AccountId).IsRequired();
        builder.Property(e => e.FarmId).IsRequired();
        builder.Property(e => e.HouseId).IsRequired();
        builder.Property(e => e.FlockId).IsRequired();
        builder.Property(e => e.Date).IsRequired();
        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(e => e.AdjustReason).HasMaxLength(DailyEntry.MaxReasonLength);
        builder.Property(e => e.VoidReason).HasMaxLength(DailyEntry.MaxReasonLength);
        // Plain text, not jsonb — provider-portability rule (tech spec): the
        // snapshot is opaque to SQL; only the API reads it.
        builder.Property(e => e.AdjustedFromJson);
        builder.Property(e => e.Version).IsConcurrencyToken();

        // Natural-key uniqueness constraint (functional spec + tech spec §6.3).
        // Partial: voiding vacates the key (#82) — at most one LIVE entry per
        // house/flock/day, any number of voided ones preserved in history.
        // Raw SQL because EF has no typed partial-index API; nameof keeps the
        // filter honest against renames (Status stores enum names as strings).
        builder.HasIndex(e => new { e.AccountId, e.FarmId, e.HouseId, e.FlockId, e.Date })
            .IsUnique()
            .HasFilter($"\"{nameof(DailyEntry.Status)}\" <> '{nameof(DailyEntryStatus.Voided)}'")
            .HasDatabaseName("IX_DailyEntries_NaturalKey");

        // Grade lines — EF reads/writes via the "_grades" backing field; removing a
        // line from the collection deletes the row (required FK -> orphan delete).
        builder.Navigation(e => e.Grades)
            .HasField("_grades")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(e => e.Grades)
            .WithOne()
            .HasForeignKey(g => g.DailyEntryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DailyEntryGradeConfiguration : IEntityTypeConfiguration<DailyEntryGrade>
{
    public void Configure(EntityTypeBuilder<DailyEntryGrade> builder)
    {
        builder.HasKey(g => g.Id);
        builder.Property(g => g.AccountId).IsRequired();
        builder.Property(g => g.DailyEntryId).IsRequired();
        builder.Property(g => g.EggGradeId).IsRequired();
        builder.Property(g => g.Quantity).IsRequired();

        // Grade rows must not disappear from under production lines.
        builder.HasOne<EggGrade>()
            .WithMany()
            .HasForeignKey(g => g.EggGradeId)
            .OnDelete(DeleteBehavior.Restrict);

        // One line per grade within an entry (domain also enforces this).
        builder.HasIndex(g => new { g.DailyEntryId, g.EggGradeId }).IsUnique();

        // The tenant query filter predicates every read on AccountId.
        builder.HasIndex(g => g.AccountId);
    }
}

public sealed class EggGradeConfiguration : IEntityTypeConfiguration<EggGrade>
{
    public void Configure(EntityTypeBuilder<EggGrade> builder)
    {
        builder.HasKey(g => g.Id);
        builder.Property(g => g.AccountId).IsRequired();
        builder.Property(g => g.FarmId).IsRequired();
        builder.Property(g => g.Name).HasMaxLength(EggGrade.MaxNameLength).IsRequired();
        builder.Property(g => g.GradeType)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // #396 — stored as a string like GradeType, for the same reason: the
        // column stays readable in a psql session and an enum reorder cannot
        // silently repoint existing rows to a different kind.
        builder.Property(g => g.DailyEntryKind)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // At most ONE Cracked and ONE Dirty grade per farm — the Daily Entry has
        // exactly one counter for each, so a second claimant makes "which grade
        // does this counter feed" ambiguous, and resolution would silently pick
        // one. Filtered so the many ordinary Manual grades are unconstrained.
        //
        // Scoped per FARM rather than per account: grade rows are farm-owned
        // (the case-insensitive name index is per farm too), so two farms in one
        // account each get their own Cracked.
        builder.HasIndex(g => new { g.AccountId, g.FarmId, g.DailyEntryKind })
            .IsUnique()
            .HasFilter("\"DailyEntryKind\" <> 'Manual'");

        builder.Property(g => g.Version).IsConcurrencyToken();

        // Name uniqueness is case-insensitive per farm — enforced by a raw
        // lower(Name) expression index (EF can't model it); see the
        // InitialCreate migration (#245 squashed the AddEggGradeManagement one
        // that introduced it). Handlers pre-check via
        // NameExistsAsync for a friendly 409; the index is the real guarantee.

        // #283 Part 1 — spec §9.1's default grades are static reference data,
        // seeded via idempotent raw SQL in the InitialCreate migration
        // (originally #283's AddBaseReferenceDataAndMustChangePassword,
        // carried by hand through #245's squash), NOT via EF's HasData():
        // HasData would put these 10 rows in the MODEL, and the grade catalog
        // is user-managed (PUT /api/v1/egg-grades/{id} renames one), so a
        // later model-diff would rename a farm's grade back or delete it. The
        // migration's guard is WHOLE-SET — "does this account have any grade
        // at all" — precisely so a renamed default is never resurrected; see
        // the migration file (PR #339 review).
    }
}
