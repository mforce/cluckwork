using Cluckwork.Domain.Catalog;

namespace Cluckwork.Application.Features.Catalog;

// #854: Commerce's packed-unit read port for peer modules. Farm settings and a
// user's stepper preference may point only at an active conversion.
[ModuleContract("Commerce")]
public interface IEggUnitConversionLookup
{
    Task<EggUnitConversionDetails?> GetByUnitAsync(EggUnit unit, CancellationToken ct);
}

[ModuleContract("Commerce")]
public sealed record EggUnitConversionDetails(Guid Id, EggUnit UnitCode, int EggsPerUnit, bool Active, int Version);
