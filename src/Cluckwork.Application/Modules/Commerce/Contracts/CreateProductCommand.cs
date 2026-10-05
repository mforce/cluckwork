namespace Cluckwork.Application.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public sealed record CreateProductCommand(
    string Name,
    string ProductType,
    string DefaultUnit,
    long? DefaultPriceMinorUnits,
    Guid? EggGradeId,
    string? Notes);
