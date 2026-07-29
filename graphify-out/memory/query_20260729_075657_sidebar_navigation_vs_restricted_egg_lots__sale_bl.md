---
type: "query"
date: "2026-07-29T07:56:57.112842+00:00"
question: "Sidebar Navigation vs Restricted Egg Lots (Sale Blocked) - is the dashboard KPI reachable via nav?"
contributor: "graphify"
source_nodes: ["Restricted Egg Lots (Sale Blocked)", "Sidebar Navigation", "Lot Sale Restriction", "Restricted Stock"]
---

# Q: Sidebar Navigation vs Restricted Egg Lots (Sale Blocked) - is the dashboard KPI reachable via nav?

## Answer

Resolved: the dashboard Restricted Lots KPI card is a drill-through surface for restriction state whose home screens ARE in the sidebar. Corroboration: egg_lots.svg has Lot Sale Restriction (EXTRACTED references from Egg Lot KPI Stat Cards; rationale: restrictions are time-bounded, sample row Until Jul 3, gating Restricted to Available transitions) and egg_inventory.svg tracks Restricted Stock as both KPI card and per-grade table column. So the AMBIGUOUS dashboard edge resolves to: Restricted Egg Lots card surfaces data owned by the Egg Lots / Egg Inventory sections, both first-class sidebar items. Not a gap - a deliberate compliance-visibility duplication (dashboard rationale: sale-blocked lots must be visible at dashboard level, not buried in the Egg Lots page).

## Source Nodes

- Restricted Egg Lots (Sale Blocked)
- Sidebar Navigation
- Lot Sale Restriction
- Restricted Stock