namespace Cluckwork.Application.Modules.Commerce.Contracts;

public sealed record CreateCustomerCommand(
    string Name,
    string Phone,
    string? Email = null,
    string? Address = null,
    string? Note = null);
