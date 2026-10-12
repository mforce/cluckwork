namespace Cluckwork.Application.Modules.Commerce.Contracts;

public sealed record UpdateProductCommand(
    Guid ProductId,
    int? Version,
    string Name,
    string DefaultUnit,
    long? DefaultPriceMinorUnits,
    Guid? EggGradeId,
    string? Notes);
