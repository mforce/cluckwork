namespace Cluckwork.Application.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public sealed record UpdateCustomerCommand(
    Guid CustomerId, int? Version, string Name, string Phone,
    string? Email = null, string? Address = null, string? Note = null);
