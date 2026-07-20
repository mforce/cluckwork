namespace Cluckwork.Infrastructure.Repositories;

using Cluckwork.Application.Features.Catalog;
using Cluckwork.Domain.Catalog;
using Cluckwork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class ProductRepository(AppDbContext db) : IProductRepository
{
    public Task<Product?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.Products.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Product>> ListAsync(bool includeInactive, CancellationToken ct = default) =>
        await db.Products
            .AsNoTracking()
            .Where(p => includeInactive || p.Active)
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .ToListAsync(ct);

    public Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken ct = default)
    {
        var normalized = name.Trim().ToLower();
        return db.Products.AnyAsync(
            p => p.Name.ToLower() == normalized && (excludeId == null || p.Id != excludeId), ct);
    }

    public async Task AddAsync(Product product, CancellationToken ct = default) =>
        await db.Products.AddAsync(product, ct);

    public Task<ProductEggGradeMapping?> GetMappingAsync(Guid productId, CancellationToken ct = default) =>
        db.ProductEggGradeMappings.FirstOrDefaultAsync(m => m.ProductId == productId, ct);

    public async Task<IReadOnlyList<ProductEggGradeMapping>> ListMappingsAsync(CancellationToken ct = default) =>
        await db.ProductEggGradeMappings.AsNoTracking().ToListAsync(ct);

    public async Task AddMappingAsync(ProductEggGradeMapping mapping, CancellationToken ct = default) =>
        await db.ProductEggGradeMappings.AddAsync(mapping, ct);
}

public sealed class EggUnitConversionRepository(AppDbContext db) : IEggUnitConversionRepository
{
    public Task<EggUnitConversion?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        db.EggUnitConversions.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<EggUnitConversion>> ListAsync(CancellationToken ct = default) =>
        await db.EggUnitConversions
            .AsNoTracking()
            .OrderBy(c => c.UnitCode)
            .ToListAsync(ct);
}
