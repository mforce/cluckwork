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
    Task<IReadOnlyList<EggUnitConversion>> ListAsync(CancellationToken ct = default);
}
