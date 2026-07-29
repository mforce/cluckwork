---
type: "query"
date: "2026-07-29T07:57:46.939039+00:00"
question: "Legacy Import Wizard vs Sales History Import Type - is sales history really an offered import type?"
contributor: "graphify"
source_nodes: ["Legacy Import Wizard", "Sales History Import Type", "Customers Import Type"]
---

# Q: Legacy Import Wizard vs Sales History Import Type - is sales history really an offered import type?

## Answer

Resolved with source verification: YES. The literal text sales history appears in specs/product/wireframes/legacy_import.svg, so the wizard does offer it as an import type alongside the four the extractor was confident about (Existing Flocks, Historical Production, Starting Inventory, Customers). The AMBIGUOUS marking (0.3) was extractor over-caution, likely due to smaller or de-emphasized styling in the SVG. The companion AMBIGUOUS edge Customers Import Type to Sales History Import Type also stands as a real relation: both are commerce-side imports and imported sales-history rows presuppose imported customers. Both edges should firm up to EXTRACTED/INFERRED on the next re-extraction.

## Source Nodes

- Legacy Import Wizard
- Sales History Import Type
- Customers Import Type