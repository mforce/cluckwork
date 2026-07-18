namespace Cluckwork.Application.Features.Inventory.UpdateWaterUsage;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

// Correcting a water record (#67, decided on #73: corrections are the gated
// surface once roles exist). Optimistic concurrency: parallel edits race on
// the Version token — the loser gets the global 409.
public sealed class UpdateWaterUsageHandler(
    IWaterUsageRepository waterUsages,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(
        UpdateWaterUsageCommand command, CancellationToken ct)
    {
        // Tenant query filter scopes the lookup.
        var usage = await waterUsages.GetByIdAsync(command.WaterUsageId, ct);
        if (usage is null)
            return Result.Failure(Error.NotFound(nameof(WaterUsage), command.WaterUsageId));

        var quantity = command.Quantity ?? command.MeterEnd!.Value - command.MeterStart!.Value;
        var source = Enum.Parse<WaterSource>(command.Source, ignoreCase: true);

        var result = usage.Update(
            quantity, command.Unit ?? usage.Unit, source,
            command.MeterStart, command.MeterEnd, command.Note);
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
