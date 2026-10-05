namespace Cluckwork.Domain.Sales;

[ModuleContract("Commerce")]
public enum PaymentMethod { Cash, Check, Card, BankTransfer, MobilePayment, Other }
