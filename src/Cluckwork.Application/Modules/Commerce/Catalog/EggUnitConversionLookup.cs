using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Commerce.Catalog;
using Cluckwork.Domain.Modules.Commerce.Contracts;

namespace Cluckwork.Application.Modules.Commerce.Catalog;

public sealed class EggUnitConversionLookup(IEggUnitConversionRepository conversions) : IEggUnitConversionLookup
{
    public async Task<EggUnitConversionDetails?> GetByUnitAsync(EggUnit unit, CancellationToken ct) =>
        await conversions.GetByUnitAsync(unit, ct) is { } conversion ? ToDetails(conversion) : null;

    internal static EggUnitConversionDetails ToDetails(EggUnitConversion c) =>
        new(c.Id, c.UnitCode, c.EggsPerUnit, c.Active, c.Version);
}
