---
type: "query"
date: "2026-07-29T07:56:57.056388+00:00"
question: "Customers Screen Wireframe vs Sales Nav Item - where does Customers live in navigation?"
contributor: "graphify"
source_nodes: ["Customers Screen Wireframe", "Sales Nav Item", "Sidebar Navigation"]
---

# Q: Customers Screen Wireframe vs Sales Nav Item - where does Customers live in navigation?

## Answer

Resolved: genuine wireframe navigation gap. Every sidebar node in the graph omits Customers - customers.svg has an 11-item sidebar (Dashboard, Daily Entry, Flocks, Egg Lots, Egg Inventory, Sales, Feed and Inventory, Health, Alerts, Reports, Settings; lines 11-24) and the v4 sidebars (dashboard.svg, sales_order.svg) have 10 items, none listing Customers - yet the Customers screen exists with toolbar and records table. The AMBIGUOUS edge encodes the extractor hypothesis that Customers nests under the Sales nav item (customers.svg line 17, EXTRACTED reference from the same sidebar). Verdict: either the sidebar is missing a Customers entry or Customers is intentionally a Sales sub-view; the wireframes never say which. Worth an IA decision.

## Source Nodes

- Customers Screen Wireframe
- Sales Nav Item
- Sidebar Navigation