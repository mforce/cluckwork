---
type: "query"
date: "2026-07-29T07:56:57.168822+00:00"
question: "Withdrawal Period Indicator vs Alerts Badge - do withdrawal periods generate alerts?"
contributor: "graphify"
source_nodes: ["Withdrawal Period Indicator", "Alerts Badge", "Create Withdrawal Alert Action", "Egg Withdrawal Restriction"]
---

# Q: Withdrawal Period Indicator vs Alerts Badge - do withdrawal periods generate alerts?

## Answer

Resolved: connected, but only through an explicit user action, never automatically. The medication_withdrawal.svg screen has a dedicated Create Withdrawal Alert Action node (implements Egg Withdrawal Restriction, INFERRED 0.85) whose rationale states creating a withdrawal alert is an explicit user action rather than an automatic side effect - a separate primary button beside Save Medication. So the flocks-list Withdrawal column and the header Alerts Badge share a data source (active withdrawal periods) but the alert only exists if someone clicked the button. Open design question the graph surfaces: should saving a medication with an egg-withdrawal date auto-create the alert? Today the wireframes say no.

## Source Nodes

- Withdrawal Period Indicator
- Alerts Badge
- Create Withdrawal Alert Action
- Egg Withdrawal Restriction