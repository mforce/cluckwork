namespace Cluckwork.Application.Modules.Commerce.Contracts;

public sealed record CustomerBalance(
    Guid CustomerId, long ConfirmedTotalMinorUnits, long PaidMinorUnits);
