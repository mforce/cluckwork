namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class InventoryItemConfiguration : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.AccountId).IsRequired();
        builder.Property(i => i.FarmId).IsRequired();
        builder.Property(i => i.Name).HasMaxLength(InventoryItem.MaxNameLength).IsRequired();
        builder.Property(i => i.Category)
            .HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(i => i.Unit).HasMaxLength(InventoryItem.MaxUnitLength).IsRequired();
        builder.Property(i => i.Version).IsConcurrencyToken();

        // Case-insensitive unique name per farm is a raw lower("Name") index in
        // the migration (same approach as EggGrades — EF can't express it).
        builder.HasIndex(i => new { i.AccountId, i.FarmId });

        // Nullable owned Money: all three columns null together = no default cost.
        builder.OwnsOne(i => i.DefaultUnitCost, m =>
        {
            m.Property(x => x.MinorUnits).HasColumnName("DefaultCostMinorUnits");
            m.Property(x => x.CurrencyCode).HasMaxLength(3).HasColumnName("DefaultCostCurrencyCode");
            m.Property(x => x.CurrencyMinorUnit).HasColumnName("DefaultCostCurrencyMinorUnit");
        });
    }
}

public sealed class InventoryLotConfiguration : IEntityTypeConfiguration<InventoryLot>
{
    public void Configure(EntityTypeBuilder<InventoryLot> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.AccountId).IsRequired();
        builder.Property(l => l.LotNumber).HasMaxLength(InventoryLot.MaxLotNumberLength);
        // Weighed quantities: 3 decimal places covers grams-in-kg / ml-in-L.
        builder.Property(l => l.QuantityReceived).HasPrecision(18, 3).IsRequired();
        builder.Property(l => l.QuantityAvailable).HasPrecision(18, 3).IsRequired();
        builder.Property(l => l.Version).IsConcurrencyToken();

        // Items with received stock cannot be deleted from under their lots.
        builder.HasOne<InventoryItem>()
            .WithMany()
            .HasForeignKey(l => l.InventoryItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.OwnsOne(l => l.UnitCost, m =>
        {
            m.Property(x => x.MinorUnits).HasColumnName("UnitCostMinorUnits").IsRequired();
            m.Property(x => x.CurrencyCode).HasMaxLength(3).HasColumnName("UnitCostCurrencyCode").IsRequired();
            m.Property(x => x.CurrencyMinorUnit).HasColumnName("UnitCostCurrencyMinorUnit").IsRequired();
        });

        // FIFO consumption scans an item's lots by received date.
        builder.HasIndex(l => new { l.InventoryItemId, l.ReceivedDate });
    }
}

public sealed class InventoryMovementConfiguration : IEntityTypeConfiguration<InventoryMovement>
{
    public void Configure(EntityTypeBuilder<InventoryMovement> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.AccountId).IsRequired();
        builder.Property(m => m.Type)
            .HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(m => m.QuantityDelta).HasPrecision(18, 3).IsRequired();
        builder.Property(m => m.Unit).HasMaxLength(InventoryItem.MaxUnitLength).IsRequired();
        builder.Property(m => m.Note).HasMaxLength(InventoryMovement.MaxNoteLength);

        builder.HasOne<InventoryItem>()
            .WithMany()
            .HasForeignKey(m => m.InventoryItemId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ledger rows must outlive nothing: lots are never deleted, but keep
        // the FK honest anyway.
        builder.HasOne<InventoryLot>()
            .WithMany()
            .HasForeignKey(m => m.InventoryLotId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Cluckwork.Domain.Flocks.Flock>()
            .WithMany()
            .HasForeignKey(m => m.FlockId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ledger browsing: per item, newest first.
        builder.HasIndex(m => new { m.InventoryItemId, m.Date });
    }
}
