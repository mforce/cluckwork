namespace Cluckwork.Application.Features.Sales.RemoveOrderItem;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

public sealed class RemoveOrderItemHandler(
    ISalesOrderRepository orders,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(Guid orderId, Guid itemId, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(orderId, ct);
        if (order is null)
            return Result.Failure(Error.NotFound(nameof(SalesOrder), orderId));

        var result = order.RemoveItem(itemId);
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
