---
kind: configuration
of: business-rule
settings:
- entity: farm
  fact: Worker sale allocation
takesEffect: When a Worker with flock assignments confirms a sale. The setting is read inside the confirmation, so a change applies to the next confirmation.
stability: A confirmed order keeps the lots it drew; changing the setting never re-allocates recorded orders.
alternatives:
- id: worker-sales-draw-assigned-flocks
  selectedWhen: Worker sale allocation is Assigned flocks only, the default for every farm.
- id: worker-sales-draw-all-flocks
  selectedWhen: Worker sale allocation is All farm flocks, an explicit Owner opt-in.
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Domain/Modules/Farm/Contracts/WorkerSaleAllocationPolicy.cs
---

# Worker sale allocation

Farms choose whether a Worker restricted to some flocks may sell eggs from the rest of the farm; both policies are supported. Owner, Manager and Sales confirmations always draw farm-wide.
