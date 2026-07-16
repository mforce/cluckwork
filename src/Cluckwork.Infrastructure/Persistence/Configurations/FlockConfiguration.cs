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
