namespace Cluckwork.Application.Features.Sales.RecordPayment;

public sealed record RecordPaymentCommand(
    Guid SalesOrderId,
    DateOnly PaymentDate,
    long AmountMinorUnits,
    string Method,
    string? ReferenceNumber,
    string? Note);
