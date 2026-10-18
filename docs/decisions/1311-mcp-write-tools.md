# MCP write tools: one scope per permission, no drafts, a correction hint (#1311, #1313)

> **Rule**: the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file holds the reasoning. It amends [#806](806-mcp-endpoint.md), which named two
> scopes, and extends [#809](809-mcp-write-idempotency.md) to every write tool.

**Status:** accepted
**Date:** 2026-10-18

## What happened

No incident. A connected app could read and record a draft daily entry, and nothing else.
#1311 asked for submitting, and #1313 for feed use, water use, inventory purchases and
expenses. The maintainer settled the shape on both issues (2026-10-18): one PR, three new
consent permissions, records that take effect at once, and replies that say where to correct.

## The rule

Each write permission is one OAuth scope, named `<resource>:<verb>` like `farm:read` and
`daily-entries:write`:

| Consent permission | Scope | Tools | Role policy |
|---|---|---|---|
| **Submit daily entries** | `daily-entries:submit` | `cluckwork_submit_daily_entry` | `ProductionWrite` |
| **Record farm operations** | `operations:write` | `cluckwork_record_feed_usage`, `cluckwork_record_water_usage`, `cluckwork_record_inventory_purchase` | `ProductionWrite` |
| **Record expenses** | `expenses:write` | `cluckwork_record_expense` | `AdminOnly` |

Submitting shares the `daily-entries` resource with recording and differs by verb, because
both act on the same record. `operations` and `expenses` are the nouns on the consent
screen. A read tool requires exactly `farm:read`; a write tool requires exactly one write
scope. Every tool calls its REST route's validator and handler through the module contract,
under that route's role policy, and runs through `McpWrite.RunAsync`: the record tool's #809
protocol, lifted out so all six writes share one copy. Breaking the one-scope rule lets a
token reach a write its person never allowed; copying a route's rule into a tool lets the
two drift.

Feed use, water use, purchases and expenses take effect at once, as the app's own buttons
do. Each reply carries `correctInApp`: fixed text naming the screen and control, in the app's
English labels, where an Admin or Manager corrects the record. A daily entry still starts as
a **Draft**; only the submit tool or a person submits it.

Feed use, water use and purchases now write an audit event (`FeedUsage.Create`,
`WaterUsage.Create`, `InventoryItem.Purchase`) in the handler's own transaction, so a record
names the person and, through #800's columns, the connected app. The REST routes write it
too, because they share the handler.

## Why not the obvious alternative

**One scope for every write.** Simpler for the app, but a person allowing drafts would also
allow money and stock. The maintainer chose three permissions so each person decides per
app, and expenses stay separate because they are money and limited to Owner and Manager.

**A draft or confirm step for operations and expenses.** Nothing in the app holds these as
drafts, so a confirm step would be a second state machine the app does not have. The stated
risk: a model that misreads a request records feed, water, a purchase or an expense that
counts at once. Three things bound it. The person allowed that permission on the consent
screen, which says these count at once. The idempotency key stops a resend from recording
twice. And the reply names where to correct it, so the app can tell the person.

**Copying the route's checks into the tool.** That is two places to change. The tools call
the contract and the validator the route resolves, and `McpRouteParityTests` holds each tool
inside its route's roles.

**Matching item and category names in the database.** The flock resolver does, through
`IFlockLookup.ResolveByNameAsync`, because flocks are numerous and flock-scoped. Items and
expense categories are a farm's short catalogs, so `McpNames.Resolve` matches the contract's
full list in memory: an exact name, ignoring case, among active records, or any record by id.

## What this does NOT cover

- **Corrections, adjustments, write-offs, voids, and item or category management.** They
  stay in the app.
- **A model sending the wrong numbers.** Validation refuses what the app refuses, and nothing
  more. The correction hint is the remedy, not a check.
- **Existing connections.** None gains a permission silently. An app reconnects; consent marks
  each new permission **New**, as for any wider request (#798). Since #1326 consent offers a
  checkbox per requested permission: **New** means not in a current approval, it starts
  unticked, and none is added without a new password-backed approval.
- **Which scopes an app asks for.** Discovery lists every scope in `scopes_supported`. An app
  that asks for a fixed list has to be told about the new ones in its own configuration.
- **A replayed purchase or expense** re-checks nothing beyond the role and scope gates. Neither
  is flock-scoped. Feed, water and submit replays re-check that the caller still sees the
  flock, as the record tool does.

## How it is enforced

- `McpWriteToolsTests`: each scope and role lists exactly its write tools; submit by id and by
  flock and date; a same-key retry makes one set of lots; feed, water, purchase and expense
  record through the handler with an audit row naming the app; notes, lot numbers and
  descriptions never come back; a restricted Worker is refused another flock; shared and
  inactive item names; a decimal cost past the currency's precision; two tools sharing a caller
  key (guard 20).
- `McpToolSurfaceTests.RealTree_EveryToolCarriesARolePolicyAndAScopePolicy`: one scope per tool,
  `farm:read` exactly for reads. `RealTree_ToolRepliesReturnOnlyReviewedText` lists every reply
  text field. `RealTree_EveryWriteToolTakesAnIdempotencyKey` covers all six writes.
- `McpRouteParityTests`: every tool mirrors its route, and every scope in `OAuthScopes.All` is
  required by a tool.
- `McpAuthorizationTests.ProtectedResourceMetadata_NamesTheIssuerAndTheScopes` pins the five
  advertised scopes.
- `SimulationSeederTests.SimulationSeed_AttributesEachAuditedActionToItsPersona` and
  `AuditVocabularyCoverageTests` cover the three new audit actions.
- `tools/oauth/mutation-check.sh` proves each can fail; the #1311 and #1313 mutants are listed
  there.
