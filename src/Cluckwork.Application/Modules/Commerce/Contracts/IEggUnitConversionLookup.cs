using Cluckwork.Domain.Modules.Commerce.Contracts;

namespace Cluckwork.Application.Modules.Commerce.Contracts;

// #854: Commerce's packed-unit read port for peer modules. Farm settings and a
// user's stepper preference may point only at an active conversion.
public interface IEggUnitConversionLookup
{
    Task<EggUnitConversionDetails?> GetByUnitAsync(EggUnit unit, CancellationToken ct);
}

public sealed record EggUnitConversionDetails(Guid Id, EggUnit UnitCode, int EggsPerUnit, bool Active, int Version);
