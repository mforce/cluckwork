namespace Cluckwork.Application.Features.Sales.VoidPayment;

[ModuleContract("Commerce")]
public sealed record VoidPaymentCommand(Guid PaymentId, int Version, string Reason);
