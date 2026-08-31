# Sales & finance

## Description

Customers, orders, FIFO allocations, payments, expenses, products.

## Tables

| Name | Columns | Comment | Type |
| ---- | ------- | ------- | ---- |
| [public.Customers](public.Customers.md) | 8 |  | BASE TABLE |
| [public.EggGrades](public.EggGrades.md) | 10 |  | BASE TABLE |
| [public.ExpenseCategories](public.ExpenseCategories.md) | 6 |  | BASE TABLE |
| [public.Products](public.Products.md) | 12 |  | BASE TABLE |
| [public.SalesOrders](public.SalesOrders.md) | 11 |  | BASE TABLE |
| [public.EggLots](public.EggLots.md) | 10 |  | BASE TABLE |
| [public.Expenses](public.Expenses.md) | 12 |  | BASE TABLE |
| [public.ProductEggGradeMappings](public.ProductEggGradeMappings.md) | 4 |  | BASE TABLE |
| [public.Payments](public.Payments.md) | 14 |  | BASE TABLE |
| [public.SalesOrderItems](public.SalesOrderItems.md) | 13 |  | BASE TABLE |
| [public.SalesOrderAllocations](public.SalesOrderAllocations.md) | 7 |  | BASE TABLE |

## Relations

```mermaid
erDiagram

"public.SalesOrders" }o--|| "public.Customers" : "FOREIGN KEY (#quot;CustomerId#quot;) REFERENCES #quot;Customers#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.EggLots" }o--|| "public.EggGrades" : "FOREIGN KEY (#quot;EggGradeId#quot;) REFERENCES #quot;EggGrades#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.Expenses" }o--|| "public.ExpenseCategories" : "FOREIGN KEY (#quot;ExpenseCategoryId#quot;) REFERENCES #quot;ExpenseCategories#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.ProductEggGradeMappings" }o--|| "public.EggGrades" : "FOREIGN KEY (#quot;EggGradeId#quot;) REFERENCES #quot;EggGrades#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.ProductEggGradeMappings" }o--|| "public.Products" : "FOREIGN KEY (#quot;ProductId#quot;) REFERENCES #quot;Products#quot;(#quot;Id#quot;) ON DELETE CASCADE"
"public.Payments" }o--|| "public.Customers" : "FOREIGN KEY (#quot;CustomerId#quot;) REFERENCES #quot;Customers#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.Payments" }o--|| "public.SalesOrders" : "FOREIGN KEY (#quot;SalesOrderId#quot;) REFERENCES #quot;SalesOrders#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.SalesOrderItems" }o--|| "public.EggGrades" : "FOREIGN KEY (#quot;EggGradeId#quot;) REFERENCES #quot;EggGrades#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.SalesOrderItems" }o--|| "public.Products" : "FOREIGN KEY (#quot;ProductId#quot;) REFERENCES #quot;Products#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.SalesOrderItems" }o--|| "public.SalesOrders" : "FOREIGN KEY (#quot;SalesOrderId#quot;) REFERENCES #quot;SalesOrders#quot;(#quot;Id#quot;) ON DELETE CASCADE"
"public.SalesOrderAllocations" }o--|| "public.SalesOrders" : "FOREIGN KEY (#quot;SalesOrderId#quot;) REFERENCES #quot;SalesOrders#quot;(#quot;Id#quot;) ON DELETE CASCADE"
"public.SalesOrderAllocations" }o--|| "public.EggLots" : "FOREIGN KEY (#quot;EggLotId#quot;) REFERENCES #quot;EggLots#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.SalesOrderAllocations" }o--|| "public.SalesOrderItems" : "FOREIGN KEY (#quot;SalesOrderItemId#quot;) REFERENCES #quot;SalesOrderItems#quot;(#quot;Id#quot;) ON DELETE CASCADE"

"public.Customers" {
  uuid Id
  varchar_200_ Name
  varchar_50_ Phone
  varchar_254_ Email
  varchar_500_ Address
  varchar_1000_ Note
  uuid AccountId
  integer Version
}
"public.EggGrades" {
  uuid Id
  uuid FarmId
  varchar_50_ Name
  varchar_16_ GradeType
  integer SortOrder
  boolean IsSaleable
  boolean Active
  varchar_16_ DailyEntryKind
  integer Version
  uuid AccountId
}
"public.ExpenseCategories" {
  uuid Id
  uuid FarmId
  varchar_100_ Name
  boolean Active
  integer Version
  uuid AccountId
}
"public.Products" {
  uuid Id
  uuid FarmId
  varchar_100_ Name
  varchar_16_ ProductType
  varchar_16_ DefaultUnit
  bigint DefaultPriceMinorUnits
  varchar_3_ CurrencyCode
  integer CurrencyMinorUnit
  varchar_500_ Notes
  boolean Active
  integer Version
  uuid AccountId
}
"public.SalesOrders" {
  uuid Id
  varchar_100_ ReferenceNumber
  uuid CustomerId
  varchar_32_ Status
  date OrderDate
  bigint TotalMinorUnits
  varchar_3_ TotalCurrencyCode
  integer TotalCurrencyMinorUnit
  varchar_500_ VoidReason
  integer Version
  uuid AccountId
}
"public.EggLots" {
  uuid Id
  uuid FlockId
  date ProductionDate
  uuid EggGradeId
  integer QuantityProduced
  integer QuantityAvailable
  uuid DailyEntryId
  date RestrictedUntil
  integer Version
  uuid AccountId
}
"public.Expenses" {
  uuid Id
  uuid FarmId
  uuid ExpenseCategoryId
  date Date
  varchar_200_ Description
  bigint AmountMinorUnits
  varchar_3_ CurrencyCode
  integer CurrencyMinorUnit
  uuid FlockId
  varchar_500_ Note
  integer Version
  uuid AccountId
}
"public.ProductEggGradeMappings" {
  uuid Id
  uuid ProductId
  uuid EggGradeId
  uuid AccountId
}
"public.Payments" {
  uuid Id
  uuid SalesOrderId
  uuid CustomerId
  date PaymentDate
  bigint AmountMinorUnits
  varchar_3_ CurrencyCode
  integer CurrencyMinorUnit
  varchar_20_ Method
  varchar_50_ ReferenceNumber
  varchar_500_ Note
  boolean Voided
  varchar_500_ VoidReason
  integer Version
  uuid AccountId
}
"public.SalesOrderItems" {
  uuid Id
  uuid SalesOrderId
  uuid ProductId
  varchar_16_ ProductTypeSnapshot
  uuid EggGradeId
  varchar_16_ Unit
  integer BaseUnitFactor
  integer Quantity
  integer QuantityBase
  bigint UnitPriceMinorUnits
  varchar_3_ UnitPriceCurrencyCode
  integer UnitPriceCurrencyMinorUnit
  uuid AccountId
}
"public.SalesOrderAllocations" {
  uuid Id
  uuid SalesOrderId
  uuid SalesOrderItemId
  uuid EggLotId
  integer Quantity
  timestamp_with_time_zone ReleasedOnUtc
  uuid AccountId
}
```

---

> Generated by [tbls](https://github.com/k1LoW/tbls)
