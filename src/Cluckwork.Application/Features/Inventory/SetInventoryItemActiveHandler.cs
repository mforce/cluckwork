namespace Cluckwork.Application.Features.Inventory;

using Cluckwork.Application.Common;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Inventory;

public sealed class SetInventoryItemActiveHandler(
    IInventoryItemRepository items,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(Guid itemId, bool active, CancellationToken ct)
    {
        var item = await items.GetByIdAsync(itemId, ct);
        if (item is null)
            return Result.Failure(Error.NotFound(nameof(InventoryItem), itemId));

        var result = active ? item.Activate() : item.Deactivate();
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
