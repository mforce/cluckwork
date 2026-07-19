namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.AccountId).IsRequired();
        builder.Property(p => p.SalesOrderId).IsRequired();
        builder.Property(p => p.CustomerId).IsRequired();
        builder.Property(p => p.PaymentDate).IsRequired();
        builder.Property(p => p.AmountMinorUnits).IsRequired();
        builder.Property(p => p.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(p => p.CurrencyMinorUnit).IsRequired();
        builder.Property(p => p.Method).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.ReferenceNumber).HasMaxLength(Payment.MaxReferenceLength);
        builder.Property(p => p.Note).HasMaxLength(Payment.MaxNoteLength);
        builder.Property(p => p.VoidReason).HasMaxLength(Payment.MaxNoteLength);
        builder.Property(p => p.Version).IsConcurrencyToken();

        // Orders and customers with money against them must not disappear.
        builder.HasOne<SalesOrder>()
            .WithMany()
            .HasForeignKey(p => p.SalesOrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(p => p.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Settlement history per order; balances group by customer.
        builder.HasIndex(p => new { p.AccountId, p.SalesOrderId });
        builder.HasIndex(p => new { p.AccountId, p.CustomerId });
    }
}
