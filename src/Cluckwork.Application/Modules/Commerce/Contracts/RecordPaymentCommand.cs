namespace Cluckwork.Application.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public sealed record RecordPaymentCommand(
    Guid SalesOrderId,
    DateOnly PaymentDate,
    long AmountMinorUnits,
    string Method,
    string? ReferenceNumber,
    string? Note);
