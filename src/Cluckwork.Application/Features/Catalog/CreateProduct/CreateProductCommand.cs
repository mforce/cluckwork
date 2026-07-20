namespace Cluckwork.Application.Features.Catalog.CreateProduct;

public sealed record CreateProductCommand(
    string Name,
    string ProductType,
    string DefaultUnit,
    long? DefaultPriceMinorUnits,
    Guid? EggGradeId,
    string? Notes);
