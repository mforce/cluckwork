namespace Cluckwork.Application.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public sealed record UpdateProductCommand(
    Guid ProductId,
    string Name,
    string DefaultUnit,
    long? DefaultPriceMinorUnits,
    Guid? EggGradeId,
    string? Notes);
