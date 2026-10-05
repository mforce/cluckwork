namespace Cluckwork.Application.Modules.Commerce.Contracts;

public sealed record UpdateEggUnitConversionCommand(Guid ConversionId, int EggsPerUnit, bool Active);
