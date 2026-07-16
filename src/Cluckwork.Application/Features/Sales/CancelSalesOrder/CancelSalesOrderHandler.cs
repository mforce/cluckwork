namespace Cluckwork.Application.Features.Sales.CancelSalesOrder;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

public sealed class CancelSalesOrderHandler(
    ISalesOrderRepository orders,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(Guid orderId, CancellationToken ct)
    {
        // Tenant query filter scopes the lookup — foreign orders read as null.
        var order = await orders.GetByIdAsync(orderId, ct);
        if (order is null)
            return Result.Failure(Error.NotFound(nameof(SalesOrder), orderId));

        var result = order.Cancel();
        if (result.IsFailure)
            return result;

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
