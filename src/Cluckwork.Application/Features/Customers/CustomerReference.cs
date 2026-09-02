namespace Cluckwork.Application.Features.Customers;

// Current display data for one customer, read from the customers the caller may
// already see (tenant filter plus the SalesFlow authorization the list and
// detail routes require) so a returned page of Sales orders names its customers
// in one grouped lookup rather than from loaded picker results (#512).
//
// Never stored on Sales Order — the order response projects it.
public sealed record CustomerReference(Guid Id, string Name);
