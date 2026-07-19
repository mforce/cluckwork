namespace Cluckwork.Application.Features.Sales.VoidPayment;

public sealed record VoidPaymentCommand(Guid PaymentId, int Version, string Reason);
