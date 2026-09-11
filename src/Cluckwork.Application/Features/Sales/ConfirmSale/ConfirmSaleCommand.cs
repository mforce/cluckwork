namespace Cluckwork.Application.Features.Sales.ConfirmSale;

// The two discount fields default so the seeders, which construct this command
// directly, keep compiling and keep confirming their at-list orders (#394).
public sealed record ConfirmSaleCommand(
    Guid SalesOrderId,
    string? DiscountReasonCode = null,
    string? DiscountReasonNote = null);

public sealed record ConfirmSaleResponse(Guid SalesOrderId, string Status);
