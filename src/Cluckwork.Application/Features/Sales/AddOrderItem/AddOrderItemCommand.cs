namespace Cluckwork.Application.Features.Sales.AddOrderItem;

// Unit defaults to the product's DefaultUnit when null; price defaults to the
// product's DefaultPriceMinorUnits when null (422 if the product has none).
public sealed record AddOrderItemCommand(
    Guid SalesOrderId, Guid ProductId, int Quantity,
    string? Unit, long? UnitPriceMinorUnits);
