namespace Cluckwork.Application.Features.Sales.AddOrderItem;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Eggs;
using Cluckwork.Domain.Sales;

public sealed class AddOrderItemHandler(
    ISalesOrderRepository orders,
    IEggGradeRepository eggGrades,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        AddOrderItemCommand command, Guid accountId, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(command.SalesOrderId, ct);
        if (order is null)
            return Result.Failure<Guid>(Error.NotFound(nameof(SalesOrder), command.SalesOrderId));

        // Tenant-scoped by the query filter; must be an active saleable grade.
        var grade = await eggGrades.GetByIdAsync(command.EggGradeId, ct);
        if (grade is null || !grade.Active || !grade.IsSaleable)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.UnknownGrade",
                "The egg grade does not exist, is inactive, or is not saleable."));

        // Item price inherits the order's snapshotted currency.
        var unitPrice = new Money(
            command.UnitPriceMinorUnits,
            order.TotalAmount.CurrencyCode,
            order.TotalAmount.CurrencyMinorUnit);

        var result = order.AddItem(command.EggGradeId, command.Quantity, unitPrice);
        if (result.IsFailure)
            return Result.Failure<Guid>(result.Error);

        // EF assigns the item id during save (deliberately not client-set).
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(result.Value.Id);
    }
}
