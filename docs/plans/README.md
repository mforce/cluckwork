# Planning records

**These are not current documentation.** Each directory is the paper trail of one
feature's design — product framing, architecture, program design, slice plan, and
the review rounds against each — captured *before and during* the work.

They describe what was **intended at the time**. Where a plan and the shipped code
disagree, the code is right and the plan is a historical record of a decision that
moved. Kept for provenance: they carry the reasoning and the rejected alternatives
that no diff shows.

| Record | Feature | Status |
|---|---|---|
| [`500-seeded-audit-actor/`](500-seeded-audit-actor/) | Seeded audit events carry a real actor (#500) | Shipped; issue closed |
| [`audit-entity-history/`](audit-entity-history/) | Entity-scoped "View history" (#493) | Shipped; issue closed |

**Where to look instead:**

| For | Go to |
|---|---|
| The rule as it stands now | [`../../AGENTS.md`](../../AGENTS.md) |
| Why a rule exists | [`../decisions/`](../decisions/) |
| How to operate it | [`../runbooks/`](../runbooks/) |
| What the product does | [`../../specs/product/specs.md`](../../specs/product/specs.md) |

A plan is finished when its work merges. Nothing here is updated afterwards —
updating it would destroy the record of what was actually believed at the time.
