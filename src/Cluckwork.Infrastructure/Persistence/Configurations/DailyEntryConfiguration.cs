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
        builder.Property(g => g.GradeCode).HasMaxLength(20).IsRequired();
        builder.Property(g => g.Quantity).IsRequired();

        // One line per grade within an entry (domain also enforces this).
        builder.HasIndex(g => new { g.DailyEntryId, g.GradeCode }).IsUnique();
    }
}
