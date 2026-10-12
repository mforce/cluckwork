using Cluckwork.Application.Common;
using Cluckwork.Application.Modules.Commerce.Contracts;
using Cluckwork.Domain.Modules.Commerce.Catalog;

namespace Cluckwork.Application.Modules.Commerce.Catalog.UpdateEggUnitConversion;

public sealed class UpdateEggUnitConversionHandler(
    IEggUnitConversionRepository conversions,
    IUnitOfWork unitOfWork,
    IAuditWriter audit)
{
    public async Task<Result> HandleAsync(UpdateEggUnitConversionCommand command, CancellationToken ct)
    {
        var conversion = await conversions.GetByIdAsync(command.ConversionId, ct);
        if (conversion is null)
            return Result.Failure(Error.NotFound("EggUnitConversion", command.ConversionId));
        if (conversion.Version != command.Version)
            return Result.Failure(Error.Conflict(
                "EggUnitConversion.VersionMismatch", "This packed unit was changed since you loaded it — reload and retry."));

        var previous = conversion.EggsPerUnit;
        var result = conversion.Update(command.EggsPerUnit, command.Active);
        if (result.IsFailure) return result;

        // Redefining what a carton/case means is exactly the kind of quiet
        // config change the trail exists for (#93); existing orders keep their
        // snapshots (spec §9.7), only future lines resolve the new factor.
        await audit.WriteAsync(AuditActions.EggUnitConversionUpdate, nameof(EggUnitConversion), conversion.Id,
            details: new
            {
                Unit = conversion.UnitCode.ToString(),
                PreviousEggsPerUnit = previous,
                NewEggsPerUnit = conversion.EggsPerUnit,
                conversion.Active,
            }, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
