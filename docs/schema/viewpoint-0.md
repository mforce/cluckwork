# Flocks & egg production

## Description

The daily egg loop — flocks, daily entries, grading, lots, movements.

## Tables

| Name | Columns | Comment | Type |
| ---- | ------- | ------- | ---- |
| [public.DailyEntries](public.DailyEntries.md) | 22 |  | BASE TABLE |
| [public.EggGrades](public.EggGrades.md) | 12 |  | BASE TABLE |
| [public.EggUnitConversions](public.EggUnitConversions.md) | 8 |  | BASE TABLE |
| [public.Flocks](public.Flocks.md) | 14 |  | BASE TABLE |
| [public.DailyEntryGrades](public.DailyEntryGrades.md) | 7 |  | BASE TABLE |
| [public.EggLots](public.EggLots.md) | 13 |  | BASE TABLE |
| [public.BirdMovements](public.BirdMovements.md) | 10 |  | BASE TABLE |
| [public.WaterUsages](public.WaterUsages.md) | 15 |  | BASE TABLE |
| [public.FeedUsages](public.FeedUsages.md) | 15 |  | BASE TABLE |
| [public.EggInventoryMovements](public.EggInventoryMovements.md) | 10 |  | BASE TABLE |

## Relations

```mermaid
erDiagram

"public.DailyEntryGrades" }o--|| "public.DailyEntries" : "FOREIGN KEY (#quot;DailyEntryId#quot;) REFERENCES #quot;DailyEntries#quot;(#quot;Id#quot;) ON DELETE CASCADE"
"public.DailyEntryGrades" }o--|| "public.EggGrades" : "FOREIGN KEY (#quot;EggGradeId#quot;) REFERENCES #quot;EggGrades#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.EggLots" }o--|| "public.EggGrades" : "FOREIGN KEY (#quot;EggGradeId#quot;) REFERENCES #quot;EggGrades#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.BirdMovements" }o--o| "public.DailyEntries" : "FOREIGN KEY (#quot;DailyEntryId#quot;) REFERENCES #quot;DailyEntries#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.BirdMovements" }o--|| "public.Flocks" : "FOREIGN KEY (#quot;FlockId#quot;) REFERENCES #quot;Flocks#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.WaterUsages" }o--o| "public.DailyEntries" : "FOREIGN KEY (#quot;DailyEntryId#quot;) REFERENCES #quot;DailyEntries#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.WaterUsages" }o--|| "public.Flocks" : "FOREIGN KEY (#quot;FlockId#quot;) REFERENCES #quot;Flocks#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.FeedUsages" }o--o| "public.DailyEntries" : "FOREIGN KEY (#quot;DailyEntryId#quot;) REFERENCES #quot;DailyEntries#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.FeedUsages" }o--|| "public.Flocks" : "FOREIGN KEY (#quot;FlockId#quot;) REFERENCES #quot;Flocks#quot;(#quot;Id#quot;) ON DELETE RESTRICT"
"public.EggInventoryMovements" }o--|| "public.EggLots" : "FOREIGN KEY (#quot;EggLotId#quot;) REFERENCES #quot;EggLots#quot;(#quot;Id#quot;) ON DELETE RESTRICT"

"public.DailyEntries" {
  uuid Id
  uuid FarmId
  uuid HouseId
  uuid FlockId
  date Date
  varchar_32_ Status
  integer TotalEggs
  integer CrackedEggs
  integer DirtyEggs
  integer DiscardedEggs
  integer MortalityCount
  uuid CrackedGradeId
  uuid DirtyGradeId
  varchar_500_ AdjustReason
  text AdjustedFromJson
  varchar_500_ VoidReason
  timestamp_with_time_zone LockedAtUtc
  integer Version
  uuid AccountId
  timestamp_with_time_zone CreatedAtUtc
  timestamp_with_time_zone UpdatedAtUtc
  bigint Sequence
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
  timestamp_with_time_zone CreatedAtUtc
  timestamp_with_time_zone UpdatedAtUtc
}
"public.EggUnitConversions" {
  uuid Id
  varchar_16_ UnitCode
  integer EggsPerUnit
  boolean Active
  integer Version
  uuid AccountId
  timestamp_with_time_zone CreatedAtUtc
  timestamp_with_time_zone UpdatedAtUtc
}
"public.Flocks" {
  uuid Id
  uuid FarmId
  uuid HouseId
  varchar_200_ Name
  varchar_100_ Breed
  date PlacementDate
  integer InitialCount
  varchar_32_ Status
  date DepletedOn
  date ArchivedOn
  integer Version
  uuid AccountId
  timestamp_with_time_zone CreatedAtUtc
  timestamp_with_time_zone UpdatedAtUtc
}
"public.DailyEntryGrades" {
  uuid Id
  uuid DailyEntryId
  uuid EggGradeId
  integer Quantity
  uuid AccountId
  timestamp_with_time_zone CreatedAtUtc
  timestamp_with_time_zone UpdatedAtUtc
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
  timestamp_with_time_zone CreatedAtUtc
  timestamp_with_time_zone UpdatedAtUtc
  bigint Sequence
}
"public.BirdMovements" {
  uuid Id
  uuid FlockId
  date Date
  varchar_16_ Type
  integer Quantity
  varchar_500_ Note
  uuid DailyEntryId
  uuid AccountId
  timestamp_with_time_zone CreatedAtUtc
  bigint Sequence
}
"public.WaterUsages" {
  uuid Id
  uuid FlockId
  date Date
  numeric_18_3_ Quantity
  varchar_8_ Unit
  varchar_32_ Source
  numeric_18_3_ MeterStart
  numeric_18_3_ MeterEnd
  varchar_500_ Note
  uuid DailyEntryId
  timestamp_with_time_zone CreatedAtUtc
  integer Version
  uuid AccountId
  timestamp_with_time_zone UpdatedAtUtc
  bigint Sequence
}
"public.FeedUsages" {
  uuid Id
  uuid FlockId
  uuid InventoryItemId
  date Date
  numeric_18_3_ Quantity
  varchar_20_ Unit
  bigint EstimatedCostMinorUnits
  varchar_3_ EstimatedCostCurrencyCode
  integer EstimatedCostCurrencyMinorUnit
  uuid DailyEntryId
  varchar_500_ Note
  timestamp_with_time_zone CreatedAtUtc
  integer Version
  uuid AccountId
  bigint Sequence
}
"public.EggInventoryMovements" {
  uuid Id
  uuid EggLotId
  varchar_16_ MovementType
  integer QuantityDelta
  varchar_50_ ReferenceType
  uuid ReferenceId
  varchar_500_ Reason
  timestamp_with_time_zone CreatedAtUtc
  uuid AccountId
  bigint Sequence
}
```

---

> Generated by [tbls](https://github.com/k1LoW/tbls)
