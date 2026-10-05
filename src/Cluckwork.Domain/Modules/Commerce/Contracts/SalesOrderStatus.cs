namespace Cluckwork.Domain.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public enum SalesOrderStatus { Draft, Confirmed, Shipped, Invoiced, Cancelled, Voided }
