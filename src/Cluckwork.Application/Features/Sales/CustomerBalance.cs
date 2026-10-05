namespace Cluckwork.Application.Features.Sales;

[ModuleContract("Commerce")]
public sealed record CustomerBalance(
    Guid CustomerId, long ConfirmedTotalMinorUnits, long PaidMinorUnits);
