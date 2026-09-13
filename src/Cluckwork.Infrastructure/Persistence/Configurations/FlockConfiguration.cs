namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Flocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class FlockConfiguration : IEntityTypeConfiguration<Flock>
{
    public void Configure(EntityTypeBuilder<Flock> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.AccountId).IsRequired();
        builder.Property(e => e.FarmId).IsRequired();
        builder.Property(e => e.HouseId).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(Flock.MaxNameLength).IsRequired();
        builder.Property(e => e.Breed).HasMaxLength(Flock.MaxBreedLength).IsRequired();
        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.Version).IsConcurrencyToken();

        builder.HasIndex(e => new { e.AccountId, e.FarmId, e.HouseId });
    }
}

public sealed class BirdMovementConfiguration : IEntityTypeConfiguration<BirdMovement>
{
    public void Configure(EntityTypeBuilder<BirdMovement> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.AccountId).IsRequired();
        builder.Property(e => e.FlockId).IsRequired();
        builder.Property(e => e.Type)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();
        builder.Property(e => e.Note).HasMaxLength(BirdMovement.MaxNoteLength);

        builder.HasOne<Flock>()
            .WithMany()
            .HasForeignKey(e => e.FlockId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Cluckwork.Domain.Eggs.DailyEntry>()
            .WithMany()
            .HasForeignKey(e => e.DailyEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ledger reads: per-flock browsing (newest first) and per-flock sums.
        builder.HasIndex(e => new { e.AccountId, e.FlockId, e.Date });
    }
}
