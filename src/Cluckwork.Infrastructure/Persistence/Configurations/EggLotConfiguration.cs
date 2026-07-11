namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Eggs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class EggLotConfiguration : IEntityTypeConfiguration<EggLot>
{
    public void Configure(EntityTypeBuilder<EggLot> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.AccountId).IsRequired();
        builder.Property(e => e.FlockId).IsRequired();
        builder.Property(e => e.GradeCode).HasMaxLength(20).IsRequired();
        builder.Property(e => e.QuantityProduced).IsRequired();
        builder.Property(e => e.QuantityAvailable).IsRequired();
        builder.Property(e => e.Version).IsConcurrencyToken();

        // Index supporting FIFO allocation queries
        builder.HasIndex(e => new { e.AccountId, e.GradeCode, e.ProductionDate, e.QuantityAvailable })
            .HasDatabaseName("IX_EggLots_Allocation");
    }
}
