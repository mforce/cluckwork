namespace Cluckwork.Infrastructure.Persistence.Configurations;

using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Eggs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.AccountId).IsRequired();
        builder.Property(p => p.FarmId).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(Product.MaxNameLength).IsRequired();
        builder.Property(p => p.ProductType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.DefaultUnit).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(p => p.CurrencyCode).HasMaxLength(3).IsRequired();
        builder.Property(p => p.Notes).HasMaxLength(Product.MaxNotesLength);
        builder.Property(p => p.Version).IsConcurrencyToken();

        // Name uniqueness is case-insensitive per account — raw lower(Name)
        // expression index carried in the InitialCreate migration (EF can't model
        // it; #245 squashed the AddProductCatalog one that introduced it);
        // NameExistsAsync pre-checks for a friendly 409.
    }
}

public sealed class ProductEggGradeMappingConfiguration : IEntityTypeConfiguration<ProductEggGradeMapping>
{
    public void Configure(EntityTypeBuilder<ProductEggGradeMapping> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.AccountId).IsRequired();

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(m => m.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<EggGrade>()
            .WithMany()
            .HasForeignKey(m => m.EggGradeId)
            .OnDelete(DeleteBehavior.Restrict);

        // Phase 1: exactly one mapping per product (multi-grade products —
        // mixed cartons — arrive later and relax this to (ProductId, EggGradeId)).
        builder.HasIndex(m => m.ProductId).IsUnique();
    }
}

public sealed class EggUnitConversionConfiguration : IEntityTypeConfiguration<EggUnitConversion>
{
    public void Configure(EntityTypeBuilder<EggUnitConversion> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.AccountId).IsRequired();
        builder.Property(c => c.UnitCode).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.Version).IsConcurrencyToken();

        // One row per unit code per account (farm overrides are a later
        // phase — the spec reserves them, the MVP is single-farm).
        builder.HasIndex(c => new { c.AccountId, c.UnitCode }).IsUnique();

        // #283 Part 1 — the default account's packed-unit defaults (#97) are
        // static reference data, seeded via idempotent raw SQL in the
        // InitialCreate migration (originally #283's
        // AddBaseReferenceDataAndMustChangePassword, carried by hand through
        // #245's squash), NOT via EF's HasData(): HasData would put these 6
        // rows in the MODEL, and EggUnitConversion.Update retunes EggsPerUnit,
        // so a later model-diff would revert the farm's own edit. The
        // migration's guard is per-key on the real unique constraint
        // (AccountId, UnitCode), not on the primary key — see the migration
        // file (PR #339 review).
    }
}
