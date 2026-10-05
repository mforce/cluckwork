namespace Cluckwork.Domain.Sales;

[ModuleContract("Commerce")]
public enum SalesOrderStatus { Draft, Confirmed, Shipped, Invoiced, Cancelled, Voided }
