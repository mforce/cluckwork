namespace Cluckwork.Application.Features.Sales.AddOrderItem;

// Unit defaults to the product's DefaultUnit when null; price defaults to the
// product's DefaultPriceMinorUnits when null (422 if the product has none).
//
// ExpectedEggsPerUnit (#445) is the eggs-per-unit factor the CALLER showed the
// user while the quantity was entered. The SPA previews "= N eggs" from a
// conversions list read at page load; an admin redefining the unit between
// that read and this write would otherwise let a seller commit a QuantityBase
// different from the one previewed — the exact silent inventory error #445
// exists to prevent. When supplied and different from the current definition,
// the write is refused (SalesOrder.UnitDefinitionChanged). Optional: raw API
// callers and the seeders, which show no preview, skip the check with null.
public sealed record AddOrderItemCommand(
    Guid SalesOrderId, Guid ProductId, int Quantity,
    string? Unit, long? UnitPriceMinorUnits, int? ExpectedEggsPerUnit = null);
