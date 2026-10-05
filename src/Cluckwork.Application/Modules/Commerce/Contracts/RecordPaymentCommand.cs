namespace Cluckwork.Application.Modules.Commerce.Contracts;

public sealed record RecordPaymentCommand(
    Guid SalesOrderId,
    DateOnly PaymentDate,
    long AmountMinorUnits,
    string Method,
    string? ReferenceNumber,
    string? Note);
