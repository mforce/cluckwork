---
appliesTo:
- type: entity
  id: egg-lot
  effect: changes
  contexts:
  - place: farm-web::farm::stock
permits:
- actors:
  - owner
  - manager
references:
- kind: code
  role: implementation
  target: src/Cluckwork.Api/AuthPolicies.cs#AuthPolicies
- kind: code
  role: implementation
  target: src/Cluckwork.Api/Modules/EggOperations/Stock/StockEndpoints.cs
---

# Only Owners and Managers correct an egg lot's stock

Write-offs and recounts on Stock are the corrective tier.
