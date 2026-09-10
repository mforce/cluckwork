namespace Cluckwork.Application.Features.Sales.UpdateOrderItem;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

public sealed class UpdateOrderItemHandler(
    ISalesOrderRepository orders,
    IAuditWriter audit,
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

        // #722 — materialised BEFORE UpdateItem. order.Items yields a
        // REFERENCE, and SalesOrderItem.Update (SalesOrder.cs:243-248) mutates
        // that same instance in place, so values read through it after the call
        // are the NEW ones and `before` would equal `after` on every row.
        // Holding the reference is not enough; the values must be read early.
        // This mirrors UpdateFarmSettingsHandler, which materialises
        // `before = Snapshot(account)` before its mutation for the same reason.
        // Pinned by UpdateItem_RecordsThePriceItChangedFromAndTo.
        //
        // The list price and basis are not mutated by Update today, and are
        // captured here anyway: a reader then needs no knowledge of which
        // fields Update happens to touch.
        var existing = order.Items.FirstOrDefault(i => i.Id == command.ItemId);
        var beforeQuantity = existing?.Quantity;
        var beforeUnitPriceMinorUnits = existing?.UnitPrice.MinorUnits;
        var beforeProductId = existing?.ProductId;
        var beforeListUnitPriceMinorUnits = existing?.ListUnitPriceMinorUnits;
        var beforeListPriceBasis = existing?.ListPriceBasis.ToString();

        var result = order.UpdateItem(command.ItemId, command.Quantity, unitPrice);
        if (result.IsFailure)
            return result;

        // #494 — see RemoveOrderItemHandler: draft-only, recorded for attribution.
        // Reaching here proves the lookup found the item: UpdateItem returns
        // Error.NotFound for the same id (SalesOrder.cs:94-95), so the captures
        // above are non-null. Ordering these two statements the other way makes
        // that false.
        await audit.WriteAsync(
            AuditActions.SalesOrderUpdateItem, nameof(SalesOrder), order.Id,
            details: new
            {
                salesOrderItemId = command.ItemId,
                productId = beforeProductId!.Value,
                before = new
                {
                    quantity = beforeQuantity!.Value,
                    unitPriceMinorUnits = beforeUnitPriceMinorUnits!.Value,
                },
                after = new
                {
                    quantity = command.Quantity,
                    unitPriceMinorUnits = unitPrice.MinorUnits,
                },
                listUnitPriceMinorUnits = beforeListUnitPriceMinorUnits,
                listPriceBasis = beforeListPriceBasis,
                currencyCode = order.TotalAmount.CurrencyCode,
                currencyMinorUnit = order.TotalAmount.CurrencyMinorUnit,
            },
            ct: ct);

        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success();
    }
}
