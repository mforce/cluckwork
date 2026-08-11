namespace Cluckwork.Application.Features.Sales.CancelSalesOrder;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;
using Microsoft.Extensions.Logging;

public sealed class CancelSalesOrderHandler(
    ISalesOrderRepository orders,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    ILogger<CancelSalesOrderHandler> logger)
{
    public async Task<Result> HandleAsync(Guid orderId, CancellationToken ct)
    {
        // Tenant query filter scopes the lookup — foreign orders read as null.
        var order = await orders.GetByIdAsync(orderId, ct);
        if (order is null)
            return Result.Failure(Error.NotFound(nameof(SalesOrder), orderId))
                .LogFailure(logger, "CancelSalesOrder");

        var result = order.Cancel();
        if (result.IsFailure)
            return result.LogFailure(logger, "CancelSalesOrder");

        // #494 — appended to THIS unit of work, after the failure returns above,
        // so a cancel that never happened leaves no trace. Cancel is Draft-only
        // and releases no stock, but it is terminal: the record must say who
        // killed it.
        await audit.WriteAsync(AuditActions.SalesOrderCancel, nameof(SalesOrder), order.Id, ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        logger.LogInformation("Sales order {SalesOrderId} cancelled", orderId);
        return Result.Success();
    }
}
