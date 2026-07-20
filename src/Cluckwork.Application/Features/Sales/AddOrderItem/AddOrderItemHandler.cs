namespace Cluckwork.Application.Features.Sales.AddOrderItem;

using Cluckwork.Application.Common;
using Cluckwork.Application.Features.Catalog;
using Cluckwork.Application.Features.EggGrades;
using Cluckwork.Application.Features.Sales;
using Cluckwork.Domain.Catalog;
using Cluckwork.Domain.Common;
using Cluckwork.Domain.Sales;

public sealed class AddOrderItemHandler(
    ISalesOrderRepository orders,
    IProductRepository products,
    IEggGradeRepository eggGrades,
    IEggUnitConversionRepository conversions,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(
        AddOrderItemCommand command, Guid accountId, CancellationToken ct)
    {
        var order = await orders.GetByIdAsync(command.SalesOrderId, ct);
        if (order is null)
            return Result.Failure<Guid>(Error.NotFound(nameof(SalesOrder), command.SalesOrderId));

        // Lines sell PRODUCTS (spec §10.5). Tenant-scoped by the query filter.
        var product = await products.GetByIdAsync(command.ProductId, ct);
        if (product is null || !product.Active)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.UnknownProduct", "The product does not exist or is inactive."));
        if (product.ProductType != ProductType.Egg)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.NotAnEggProduct", "Only egg products can be sold in this phase."));

        // The grade snapshot comes from the product's CURRENT mapping — the
        // line keeps it even if the mapping is re-pointed later.
        var mapping = await products.GetMappingAsync(product.Id, ct);
        var grade = mapping is null ? null : await eggGrades.GetByIdAsync(mapping.EggGradeId, ct);
        if (grade is null || !grade.Active || !grade.IsSaleable)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.UnknownGrade",
                "The product's egg grade does not exist, is inactive, or is not saleable."));

        // Factor resolves from the account's ACTIVE conversion at line creation
        // and is snapshotted (spec §9.7) — a later carton redefinition never
        // reinterprets this line.
        var unit = command.Unit is { } u
            ? Enum.Parse<ProductUnit>(u, ignoreCase: true)
            : product.DefaultUnit;
        var conversion = await conversions.GetByUnitAsync(EggUnits.ToConversionUnit(unit), ct);
        if (conversion is null || !conversion.Active)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.NoUnitConversion",
                $"No active eggs-per-unit definition for '{unit}' — set one on the Products screen."));

        // Price defaults from the product (per selling unit).
        var priceMinorUnits = command.UnitPriceMinorUnits ?? product.DefaultPriceMinorUnits;
        if (priceMinorUnits is null)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.PriceRequired",
                "The product has no default price — a unit price is required."));
        // The validator's overflow guard only sees an explicit price — the
        // product-default path needs the same check (Money.Multiply is unchecked).
        if (priceMinorUnits.Value > long.MaxValue / command.Quantity)
            return Result.Failure<Guid>(Error.Validation(
                "SalesOrder.LineTotalTooLarge", "Line total exceeds the supported amount range."));

        // Item price inherits the order's snapshotted currency.
        var unitPrice = new Money(
            priceMinorUnits.Value,
            order.TotalAmount.CurrencyCode,
            order.TotalAmount.CurrencyMinorUnit);

        var result = order.AddItem(
            product.Id, product.ProductType, grade.Id,
            unit, conversion.EggsPerUnit, command.Quantity, unitPrice);
        if (result.IsFailure)
            return Result.Failure<Guid>(result.Error);

        // EF assigns the item id during save (deliberately not client-set).
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Success(result.Value.Id);
    }
}
