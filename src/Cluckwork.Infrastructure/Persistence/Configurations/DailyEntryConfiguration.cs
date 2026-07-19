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

        // Natural-key uniqueness constraint (functional spec + tech spec §6.3)
        builder.HasIndex(e => new { e.AccountId, e.FarmId, e.HouseId, e.FlockId, e.Date })
            .IsUnique()
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

        builder.Property(g => g.Version).IsConcurrencyToken();

        // Name uniqueness is case-insensitive per farm — enforced by a raw
        // lower(Name) expression index (EF can't model it); see the
        // AddEggGradeManagement migration. Handlers pre-check via
        // NameExistsAsync for a friendly 409; the index is the real guarantee.
    }
}
