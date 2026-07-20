namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Eggs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class EggInventoryMovementConfiguration : IEntityTypeConfiguration<EggInventoryMovement>
{
    public void Configure(EntityTypeBuilder<EggInventoryMovement> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.AccountId).IsRequired();
        builder.Property(m => m.MovementType)
            .HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(m => m.QuantityDelta).IsRequired();
        builder.Property(m => m.ReferenceType)
            .HasMaxLength(EggInventoryMovement.MaxReferenceTypeLength).IsRequired();
        builder.Property(m => m.Reason).HasMaxLength(EggInventoryMovement.MaxReasonLength);

        // The ledger must not survive its lot's deletion as an orphan — but
        // lots are never deleted (Restrict mirrors the allocation FK).
        builder.HasOne<EggLot>()
            .WithMany()
            .HasForeignKey(m => m.EggLotId)
            .OnDelete(DeleteBehavior.Restrict);

        // The one read path: a lot's history, newest first.
        builder.HasIndex(m => new { m.AccountId, m.EggLotId, m.CreatedAtUtc });

        // Append-only: no Version, no concurrency token, no update surface.
    }
}
