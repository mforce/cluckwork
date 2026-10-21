---
relations:
- entity: inventory-lot
  verb: has
  cardinality: one-to-many
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/GeneralInventory/Inventory/InventoryItem.cs
---

# Inventory item

A catalog entry for something the farm stocks: feed, supplements, additives, medication, vaccines, packaging, bedding, sanitation, equipment parts and more.

## Information kept

- **Name** — unique within the farm, ignoring case
- **Category** — Feed, Supplement, Additive, Medication, Vaccine, Packaging, Bedding, Sanitation, EquipmentPart or Other
- **Unit of measure** — the unit quantities are kept in; locked once stock has been received
- **Default unit cost** — an optional cost per unit
- **Stock on hand** — the sum of the item's lots' remaining quantities

## States

### Active

Accepts purchases.

### Inactive

Accepts no new purchases; remaining stock can still be used up.
