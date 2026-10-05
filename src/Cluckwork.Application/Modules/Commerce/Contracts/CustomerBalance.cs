namespace Cluckwork.Application.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public sealed record CustomerBalance(
    Guid CustomerId, long ConfirmedTotalMinorUnits, long PaidMinorUnits);
