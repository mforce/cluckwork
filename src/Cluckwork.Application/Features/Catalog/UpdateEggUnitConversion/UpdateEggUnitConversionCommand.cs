namespace Cluckwork.Application.Features.Catalog.UpdateEggUnitConversion;

[ModuleContract("Commerce")]
public sealed record UpdateEggUnitConversionCommand(Guid ConversionId, int EggsPerUnit, bool Active);
