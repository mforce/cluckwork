namespace Cluckwork.Application.Features.Catalog.UpdateProduct;

[ModuleContract("Commerce")]
public sealed record UpdateProductCommand(
    Guid ProductId,
    string Name,
    string DefaultUnit,
    long? DefaultPriceMinorUnits,
    Guid? EggGradeId,
    string? Notes);
