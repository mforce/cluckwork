namespace Cluckwork.Application.Modules.Commerce.Contracts;

public sealed record VoidPaymentCommand(Guid PaymentId, int Version, string Reason);
