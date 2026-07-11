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
    }
}
