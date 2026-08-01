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
        // expression index added in the AddProductCatalog migration (EF can't
        // model it); NameExistsAsync pre-checks for a friendly 409.
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
        // AddBaseReferenceDataAndMustChangePassword migration, NOT via EF's
        // HasData(): every real deployment already ran the old runtime
        // DatabaseSeeder at least once, which minted these 6 rows with RANDOM
        // ids (EggUnitConversion.Defaults -> Create(Guid.NewGuid(), ...)) —
        // HasData's InsertData is keyed by PRIMARY KEY, so it can't detect a
        // pre-existing row under a different id and collides on the real
        // unique constraint (AccountId, UnitCode) instead. See the migration
        // file (PR #339 review).
    }
}
