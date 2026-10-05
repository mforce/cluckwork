namespace Cluckwork.Application.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public sealed record VoidPaymentCommand(Guid PaymentId, int Version, string Reason);
