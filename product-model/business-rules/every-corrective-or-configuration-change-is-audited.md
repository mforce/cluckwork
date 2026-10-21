---
appliesTo:
- type: capability
  id: create-flock
- type: capability
  id: edit-flock
- type: capability
  id: deplete-flock
- type: capability
  id: archive-flock
- type: capability
  id: reactivate-flock
- type: capability
  id: record-bird-movement
- type: capability
  id: save-daily-entry-draft
- type: capability
  id: submit-daily-entry
- type: capability
  id: adjust-daily-entry
- type: capability
  id: void-daily-entry
- type: capability
  id: write-off-stock
- type: capability
  id: create-egg-grade
- type: capability
  id: edit-egg-grade
- type: capability
  id: deactivate-egg-grade
- type: capability
  id: activate-egg-grade
- type: capability
  id: create-product
- type: capability
  id: edit-product
- type: capability
  id: deactivate-product
- type: capability
  id: activate-product
- type: capability
  id: edit-packed-unit
- type: capability
  id: create-customer
- type: capability
  id: edit-customer
- type: capability
  id: create-sales-order
- type: capability
  id: add-order-line
- type: capability
  id: edit-order-line
- type: capability
  id: remove-order-line
- type: capability
  id: cancel-sales-order
- type: capability
  id: confirm-sales-order
- type: capability
  id: void-sales-order
- type: capability
  id: void-payment
- type: capability
  id: record-feed-usage
- type: capability
  id: record-water-usage
- type: capability
  id: record-purchase
- type: capability
  id: record-expense
- type: capability
  id: correct-expense
- type: capability
  id: deactivate-expense-category
- type: capability
  id: reactivate-expense-category
- type: capability
  id: correct-water-usage
- type: capability
  id: correct-inventory-stock
- type: capability
  id: create-user
- type: capability
  id: edit-user
- type: capability
  id: reset-user-password
- type: capability
  id: change-password
- type: capability
  id: change-user-role
- type: capability
  id: change-user-email
- type: capability
  id: disable-user
- type: capability
  id: enable-user
- type: capability
  id: assign-flock
- type: capability
  id: unassign-flock
- type: capability
  id: connect-app
- type: capability
  id: disconnect-app
- type: capability
  id: turn-on-connected-apps
- type: capability
  id: turn-off-connected-apps
- type: capability
  id: change-farm-settings
- type: capability
  id: set-farm-logo
- type: capability
  id: clear-farm-logo
- type: capability
  id: set-farm-banner
- type: capability
  id: clear-farm-banner
- type: capability
  id: download-dataset
- type: capability
  id: download-full-export
- type: capability
  id: provision-farm
- type: capability
  id: provision-first-admin
- type: capability
  id: suspend-farm
- type: capability
  id: reactivate-farm
- type: capability
  id: change-farm-code
- type: capability
  id: recover-admin
- type: entity
  id: audit-event
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Auditing/AuditEvent.cs
- kind: code
  role: implementation
  target: src/Cluckwork.Application/Common/AuditActions.cs#FeedUsageCreate
- kind: doc
  role: context
  target: docs/decisions/500-audit-actor.md
- kind: doc
  role: intent
  target: specs/product/GLOSSARY.md
---

# Every corrective, destructive or configuration change is recorded in the audit log

Each change is recorded in the same transaction as the change itself, naming the signed-in person or, for an operator verb, a named system actor, with the reason the change carried. Feed use, water use and purchases are recorded too, so each names who recorded it and through which connected app. There is no way to edit or delete an event. A Sales order or Daily entry promotion by its own creator is folded into the record's creation in its record history.
