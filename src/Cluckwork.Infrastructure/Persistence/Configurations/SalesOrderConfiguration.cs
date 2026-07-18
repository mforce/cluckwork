namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class SalesOrderConfiguration : IEntityTypeConfiguration<SalesOrder>
{
    public void Configure(EntityTypeBuilder<SalesOrder> builder)
    {
        builder.HasKey(o => o.Id);
        builder.Property(o => o.AccountId).IsRequired();
        builder.Property(o => o.ReferenceNumber).HasMaxLength(100).IsRequired();
        builder.Property(o => o.Status)
            .HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(o => o.VoidReason).HasMaxLength(SalesOrder.MaxVoidReasonLength);
        builder.Property(o => o.Version).IsConcurrencyToken();

        // Reference numbers are 8-hex truncations — the index turns a birthday
        // collision into a retryable 409 instead of silent duplicates.
        builder.HasIndex(o => new { o.AccountId, o.ReferenceNumber }).IsUnique();

        // Customers with order history cannot be deleted from under them.
        builder.HasOne<Cluckwork.Domain.Sales.Customer>()
            .WithMany()
            .HasForeignKey(o => o.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Money value object stored as owned type (no jsonb — stays provider-neutral)
        builder.OwnsOne(o => o.TotalAmount, m =>
        {
            m.Property(x => x.MinorUnits).HasColumnName("TotalMinorUnits").IsRequired();
            m.Property(x => x.CurrencyCode).HasMaxLength(3).HasColumnName("TotalCurrencyCode").IsRequired();
            m.Property(x => x.CurrencyMinorUnit).HasColumnName("TotalCurrencyMinorUnit").IsRequired();
        });

        // Items navigation — EF Core reads/writes via the "_items" backing field
        builder.Navigation(o => o.Items)
            .HasField("_items")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(o => o.Items)
            .WithOne()
            .HasForeignKey(i => i.SalesOrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SalesOrderItemConfiguration : IEntityTypeConfiguration<SalesOrderItem>
{
    public void Configure(EntityTypeBuilder<SalesOrderItem> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.AccountId).IsRequired();
        builder.Property(i => i.SalesOrderId).IsRequired();
        builder.Property(i => i.EggGradeId).IsRequired();

        // Same integrity as egg lots: grade rows must not disappear from under
        // historical sales lines.
        builder.HasOne<Cluckwork.Domain.Eggs.EggGrade>()
            .WithMany()
            .HasForeignKey(i => i.EggGradeId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(i => i.Quantity).IsRequired();

        builder.OwnsOne(i => i.UnitPrice, m =>
        {
            m.Property(x => x.MinorUnits).HasColumnName("UnitPriceMinorUnits").IsRequired();
            m.Property(x => x.CurrencyCode).HasMaxLength(3).HasColumnName("UnitPriceCurrencyCode").IsRequired();
            m.Property(x => x.CurrencyMinorUnit).HasColumnName("UnitPriceCurrencyMinorUnit").IsRequired();
        });

        // LineTotal is computed — ignored by EF Core
        builder.Ignore(i => i.LineTotal);
    }
}

// Lot-level allocation provenance (#60). Written in the confirm transaction
// and never deleted — a void marks rows released (ReleasedOnUtc) so the
// sale → lot traceability chain (spec §9.6) survives the undo.
public sealed class SalesOrderAllocationConfiguration : IEntityTypeConfiguration<SalesOrderAllocation>
{
    public void Configure(EntityTypeBuilder<SalesOrderAllocation> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.AccountId).IsRequired();
        builder.Property(a => a.Quantity).IsRequired();

        builder.HasOne<SalesOrder>()
            .WithMany()
            .HasForeignKey(a => a.SalesOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<SalesOrderItem>()
            .WithMany()
            .HasForeignKey(a => a.SalesOrderItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // The source lot must not disappear from under an active allocation —
        // its quantities could then never be restored.
        builder.HasOne<Cluckwork.Domain.Eggs.EggLot>()
            .WithMany()
            .HasForeignKey(a => a.EggLotId)
            .OnDelete(DeleteBehavior.Restrict);

        // Void loads all rows for one order.
        builder.HasIndex(a => a.SalesOrderId);
    }
}

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Cluckwork.Domain.Sales.Customer>
{
    public void Configure(EntityTypeBuilder<Cluckwork.Domain.Sales.Customer> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.AccountId).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(Cluckwork.Domain.Sales.Customer.MaxNameLength).IsRequired();
        builder.Property(c => c.Phone).HasMaxLength(Cluckwork.Domain.Sales.Customer.MaxPhoneLength).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(Cluckwork.Domain.Sales.Customer.MaxEmailLength);
        builder.Property(c => c.Address).HasMaxLength(Cluckwork.Domain.Sales.Customer.MaxAddressLength);
        builder.Property(c => c.Note).HasMaxLength(Cluckwork.Domain.Sales.Customer.MaxNoteLength);

        builder.HasIndex(c => new { c.AccountId, c.Name });
    }
}
