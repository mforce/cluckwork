namespace Cluckwork.Application.Features.Sales.UpdateOrderItem;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

public sealed class UpdateOrderItemHandler(
    ISalesOrderRepository orders,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(UpdateOrderItemCommand command, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(command.SalesOrderId, ct);
        if (order is null)
            return Result.Failure(Error.NotFound(nameof(SalesOrder), command.SalesOrderId));

        // Price inherits the order's snapshotted currency, same as AddOrderItem.
        var unitPrice = new Money(
            command.UnitPriceMinorUnits,
            order.TotalAmount.CurrencyCode,
            order.TotalAmount.CurrencyMinorUnit);

        var result = order.UpdateItem(command.ItemId, command.Quantity, unitPrice);
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
