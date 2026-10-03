using Cluckwork.Domain.Catalog;

namespace Cluckwork.Application.Features.Catalog;

public sealed class EggUnitConversionLookup(IEggUnitConversionRepository conversions) : IEggUnitConversionLookup
{
    public async Task<EggUnitConversionDetails?> GetByUnitAsync(EggUnit unit, CancellationToken ct) =>
        await conversions.GetByUnitAsync(unit, ct) is { } conversion ? ToDetails(conversion) : null;

    internal static EggUnitConversionDetails ToDetails(EggUnitConversion c) =>
        new(c.Id, c.UnitCode, c.EggsPerUnit, c.Active, c.Version);
}
