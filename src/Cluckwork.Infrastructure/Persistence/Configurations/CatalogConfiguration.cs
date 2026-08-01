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
    // #283 Part 1 — fixed ids, same convention as the other static seed rows.
    // Spec §9.7's suggested defaults (EggUnitConversion.Defaults) mint
    // Guid.NewGuid() ids — fine for a runtime seeder, but HasData needs
    // stable values that don't drift on every `dotnet ef migrations add`
    // regeneration, so they're spelled out here instead of reusing that
    // factory.
    private static readonly Guid IndividualId = new("00000010-0000-0000-0000-000000000001");
    private static readonly Guid DozenId = new("00000010-0000-0000-0000-000000000002");
    private static readonly Guid FlatId = new("00000010-0000-0000-0000-000000000003");
    private static readonly Guid TrayId = new("00000010-0000-0000-0000-000000000004");
    private static readonly Guid CartonId = new("00000010-0000-0000-0000-000000000005");
    private static readonly Guid CaseId = new("00000010-0000-0000-0000-000000000006");

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
        // static reference data exactly like roles/egg grades/the account
        // itself: no runtime seeder, deterministic, multi-instance-safe.
        // Mirrors EggUnitConversion.Defaults' values (spec §9.7).
        builder.HasData(
            UnitRow(IndividualId, EggUnit.Individual, 1),
            UnitRow(DozenId, EggUnit.Dozen, 12),
            UnitRow(FlatId, EggUnit.Flat, 30),
            UnitRow(TrayId, EggUnit.Tray, 30),
            UnitRow(CartonId, EggUnit.Carton, 12),
            UnitRow(CaseId, EggUnit.Case, 360));
    }

    private static object UnitRow(Guid id, EggUnit unitCode, int eggsPerUnit) => new
    {
        Id = id,
        AccountId = Cluckwork.Domain.Accounts.SeedDefaults.AccountId,
        UnitCode = unitCode,
        EggsPerUnit = eggsPerUnit,
        Active = true,
        Version = 0,
    };
}
