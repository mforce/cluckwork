namespace Cluckwork.Application.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public sealed record UpdateEggUnitConversionCommand(Guid ConversionId, int EggsPerUnit, bool Active);
