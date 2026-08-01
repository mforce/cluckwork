namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Accounts;
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
    // #283 Part 1 — fixed ids for the 10 default grades, same fixed-GUID
    // convention as SeedDefaults (…001, …002, …). Stable across regenerations:
    // a `dotnet ef migrations add` re-run after a rebase must keep producing
    // the SAME InsertData values, or the model snapshot would drift on every
    // regeneration for no reason.
    private static readonly Guid SmallId = new("0000000e-0000-0000-0000-000000000001");
    private static readonly Guid MediumId = new("0000000e-0000-0000-0000-000000000002");
    private static readonly Guid LargeId = new("0000000e-0000-0000-0000-000000000003");
    private static readonly Guid JumboId = new("0000000e-0000-0000-0000-000000000004");
    private static readonly Guid SecondsId = new("0000000e-0000-0000-0000-000000000005");
    private static readonly Guid CrackedId = new("0000000e-0000-0000-0000-000000000006");
    private static readonly Guid DirtyId = new("0000000e-0000-0000-0000-000000000007");
    private static readonly Guid SoftShellId = new("0000000e-0000-0000-0000-000000000008");
    private static readonly Guid DiscardedId = new("0000000e-0000-0000-0000-000000000009");
    private static readonly Guid InternalUseId = new("0000000e-0000-0000-0000-000000000010");

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

        builder.Property(g => g.Version).IsConcurrencyToken();

        // Name uniqueness is case-insensitive per farm — enforced by a raw
        // lower(Name) expression index (EF can't model it); see the
        // AddEggGradeManagement migration. Handlers pre-check via
        // NameExistsAsync for a friendly 409; the index is the real guarantee.

        // #283 Part 1 — spec §9.1's default grades are static reference data,
        // baked into the migration via HasData exactly like the default
        // account above: deterministic, multi-instance-safe, no runtime
        // seeder. Same names/types/saleability/order the old DatabaseSeeder
        // wrote via EggGrade.Create; HasData can't call the domain factory
        // (private constructor, and it also throws — inappropriate for
        // static data whose shape is already known-valid), so the rows are
        // spelled out directly. Saleable grades are what daily-entry grade
        // lines may reference; the non-saleable buckets exist for future use.
        builder.HasData(
            GradeRow(SmallId, "Small", EggGradeType.Size, 0, isSaleable: true),
            GradeRow(MediumId, "Medium", EggGradeType.Size, 1, isSaleable: true),
            GradeRow(LargeId, "Large", EggGradeType.Size, 2, isSaleable: true),
            GradeRow(JumboId, "Jumbo", EggGradeType.Size, 3, isSaleable: true),
            GradeRow(SecondsId, "Seconds", EggGradeType.Quality, 4, isSaleable: true),
            GradeRow(CrackedId, "Cracked", EggGradeType.Quality, 5, isSaleable: false),
            GradeRow(DirtyId, "Dirty", EggGradeType.Quality, 6, isSaleable: false),
            GradeRow(SoftShellId, "Soft Shell", EggGradeType.Quality, 7, isSaleable: false),
            GradeRow(DiscardedId, "Discarded", EggGradeType.Custom, 8, isSaleable: false),
            GradeRow(InternalUseId, "Internal Use", EggGradeType.Custom, 9, isSaleable: false));
    }

    private static object GradeRow(
        Guid id, string name, EggGradeType gradeType, int sortOrder, bool isSaleable) => new
        {
            Id = id,
            AccountId = SeedDefaults.AccountId,
            FarmId = SeedDefaults.FarmId,
            Name = name,
            GradeType = gradeType,
            SortOrder = sortOrder,
            IsSaleable = isSaleable,
            Active = true,
            Version = 0,
        };
}
