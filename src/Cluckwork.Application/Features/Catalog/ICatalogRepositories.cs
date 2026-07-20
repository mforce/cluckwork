namespace Cluckwork.Application.Features.Catalog;

using Cluckwork.Domain.Catalog;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Product>> ListAsync(bool includeInactive, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default);
    Task AddAsync(Product product, CancellationToken ct = default);
    Task<ProductEggGradeMapping?> GetMappingAsync(Guid productId, CancellationToken ct = default);
    Task<IReadOnlyList<ProductEggGradeMapping>> ListMappingsAsync(CancellationToken ct = default);
    Task AddMappingAsync(ProductEggGradeMapping mapping, CancellationToken ct = default);
}

public interface IEggUnitConversionRepository
{
    Task<EggUnitConversion?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<EggUnitConversion?> GetByUnitAsync(EggUnit unit, CancellationToken ct = default);
    Task<IReadOnlyList<EggUnitConversion>> ListAsync(CancellationToken ct = default);
}

public static class EggUnits
{
    // Product selling units ↔ conversion unit codes. "Egg" is the individual
    // egg; the non-egg units (Bird/Lb/...) have no conversion by design.
    public static EggUnit ToConversionUnit(ProductUnit unit) => unit switch
    {
        ProductUnit.Egg => EggUnit.Individual,
        ProductUnit.Dozen => EggUnit.Dozen,
        ProductUnit.Flat => EggUnit.Flat,
        ProductUnit.Tray => EggUnit.Tray,
        ProductUnit.Carton => EggUnit.Carton,
        ProductUnit.Case => EggUnit.Case,
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Not an egg selling unit."),
    };
}
