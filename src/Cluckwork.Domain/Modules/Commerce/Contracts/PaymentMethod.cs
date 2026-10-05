namespace Cluckwork.Domain.Modules.Commerce.Contracts;

[ModuleContract("Commerce")]
public enum PaymentMethod { Cash, Check, Card, BankTransfer, MobilePayment, Other }
