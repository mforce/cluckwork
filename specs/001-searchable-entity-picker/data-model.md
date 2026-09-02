# Phase 1 Data Model: Searchable Paged Entity Picker

## Persistence Impact

No persistent entity, column, index, relationship, or migration is added. Names and flock status are current read projections, not historical snapshots. Existing aggregate versions and write behavior are unchanged.

## Backend Read Concepts

### FlockEligibility

Application-level read policy used by flock discovery.

| Value | Included statuses | Default use |
|---|---|---|
| `Active` / `active` | Active | New worker assignments |
| `ActiveAndDepleted` / `active-and-depleted` | Active, Depleted | Omitted eligibility; Daily Entry, Feed capture, Water capture |
| `All` / `all` | Active, Depleted, Archived | History/Feed/Water filters and Expenses |

Validation rules:

- Wire parsing is case-sensitive to the documented lowercase values.
- Unknown values are invalid.
- Omission maps to `ActiveAndDepleted`.
- Legacy `includeArchived=true` maps to `All` only when `eligibility` is absent.
- Presence of both `eligibility` and `includeArchived`, including explicit `includeArchived=false`, is invalid.

### FlockReference

Scoped read projection for row enrichment.

| Field | Type | Rules |
|---|---|---|
| `Id` | UUID | Canonical flock identifier; unique within result |
| `Name` | string | Current display name; non-empty |
| `Status` | FlockStatus | Current Active, Depleted, or Archived status |

Relationships:

- Built only from `Flocks` visible through current tenant/flock filters.
- Referenced by Daily Entry, Feed Usage, Water Usage, User assignment, and Expense response projections.
- Never stored on those aggregates.

### CustomerReference

Scoped read projection for Sales row enrichment.

| Field | Type | Rules |
|---|---|---|
| `Id` | UUID | Canonical customer identifier; unique within result |
| `Name` | string | Current display name; non-empty |

Relationships:

- Built only from Customers visible through current tenant and SalesFlow authorization.
- Referenced by Sales Order response projections.
- Never stored on Sales Order.

### Additive Row Response Fields

| Response | Added fields | Null meaning |
|---|---|---|
| Daily Entry | `flockName: string?`, `flockStatus: string?` | Null only when the scoped reference read cannot resolve a non-null row reference |
| Feed Usage | `flockName: string?` | Null only when the scoped reference read cannot resolve a non-null row reference |
| Water Usage | `flockName: string?` | Null only when the scoped reference read cannot resolve a non-null row reference |
| Flock Assignment | `flockName: string?` | Farm-wide when `flockId` is null; defensive unavailable display when a non-null reference is inaccessible |
| Sales Order | `customerName: string?` | Null only when the scoped reference read cannot resolve a non-null row reference |
| Expense | `flockName: string?` | No flock when `flockId` is null; defensive unavailable display when a non-null reference is inaccessible |

Required references are resolved through scoped bulk reads. If a referential or
scoping race unexpectedly leaves a required name unresolved, the response keeps
the canonical ID and returns a defensive null name; the SPA renders its explicit
unavailable label. Neither layer invents a label or exposes an identifier fragment.

## Picker Transport Models

### NamedEntityPage&lt;T&gt;

Transient result window represented by the existing bare-array response.

| Field | Type | Rules |
|---|---|---|
| `items` | `T[]` | Stable server order; deduplicated by ID when appended |
| `serverCount` | integer | Count returned by the server before deduplication; advances the offset cursor |
| `nextOffset` | integer | Sum of server counts, never unique rendered count |
| `hasMore` | boolean | True only when the last server page contains exactly 50 items |

A final empty extension after a full last page sets `hasMore=false` and does not loop.

### PickerSelection&lt;T&gt;

The picker-owned committed or externally requested identity.

| Field | Type | Rules |
|---|---|---|
| `entity` | `T?` | Full typed Flock/Customer when committed; supplies display name and typed page data |
| `requestedId` | UUID? | External identity currently resolving or unavailable |
| `phase` | SelectionPhase | Exactly one phase below |
| `transitionGeneration` | integer | Monotonic owner token checked after every async boundary |

`SelectionPhase` values:

- `uninitialized`: no lifecycle/default decision has run.
- `resolving`: exact or default resolution is current.
- `committed`: a named entity is committed.
- `blank`: an optional picker has a deliberate null value.
- `unavailable`: a well-formed external ID is missing, inaccessible, or ineligible.

### DiscoveryState&lt;T&gt;

Search/result state independent from the committed selection.

| Field | Type | Rules |
|---|---|---|
| `rawQuery` | string | Visible editable input |
| `normalizedQuery` | string? | Trimmed query; null for blank |
| `eligibilityKey` | string? | Adapter-owned flock policy; absent for customer |
| `items` | `T[]` | Rows owned by current discovery generation only |
| `activeId` | UUID? | Keyboard-active option; does not imply commit |
| `cursor` | integer | Offset advanced by raw server row count |
| `hasMore` | boolean | Whether extension may be requested |
| `phase` | DiscoveryPhase | Exactly one phase below |
| `error` | error? | Replacement or extension error owned by current generation |
| `discoveryGeneration` | integer | Monotonic intent token |

`DiscoveryPhase` values:

- `closed`
- `debouncing`
- `replacing`
- `ready`
- `empty`
- `replacement-error`
- `extending`
- `extension-error`

### PickerSnapshot&lt;T&gt;

Adapter-to-page contract emitted whenever committed or safety state changes.

| Field | Type | Derivation |
|---|---|---|
| `committed` | `T?` | From `PickerSelection.entity` |
| `selectionPhase` | SelectionPhase | Current selection lifecycle |
| `exploring` | boolean | Visible text differs from committed label, or optional blank is being edited |
| `canSubmit` | boolean | Required selection is committed and picker is not exploring/resolving/unavailable; optional selection is blank or committed and not exploring |

Pages must enforce `canSubmit` both by disabling visible write controls and by guarding submit handlers.

## State Transitions

### Discovery Generation

| Event | Immediate transition | Allowed completion |
|---|---|---|
| Raw query or eligibility changes | Increment generation; hide old items/errors; enter `debouncing` | Current generation may replace rows after debounce |
| Replacement starts | `replacing` | Success -> `ready`/`empty`; failure -> `replacement-error` |
| Load more starts | `extending`; retain items | Success -> append/dedupe and `ready`; failure -> `extension-error` retaining items/cursor |
| Retry replacement | New generation and replacement | Same as replacement |
| Retry extension | Same intent, new owned request | Same as extension |
| Stale success/error/finally | No state change | Never commits |

### Selection Transition Generation

| Event | Transition |
|---|---|
| URL/external ID changes | Increment; exact resolve; commit only if current and admitted, else explicit unavailable |
| Dialog opens/closes or page resets | Increment; blank/default/exact transition defined by page contract |
| Default requested | Increment; fetch first eligible entity under explicit policy; never derive from unrelated visible options |
| User commits Enter/click | Increment; commit typed entity and restore its label |
| User presses Escape | Increment; restore prior committed label/entity |
| Optional user clears | Increment; commit `blank` |
| Create succeeds | Increment; preserve returned ID; exact hydrate only |
| Create hydration retry | Increment; repeat GET only, never POST |
| Stale completion | No state change |

## Admission Rules

- Daily Entry, Feed capture, and new Water capture admit Active or Depleted flocks.
- History/Feed/Water filters and Expenses admit all flock statuses.
- A disabled existing Water edit may retain an exactly resolved Archived flock but cannot discover/commit a different Archived flock for new capture.
- New User assignment admits Active only; existing assignment rows are display data, not selectable defaults.
- Customer order and Sales filter discovery admit all visible customers.
- A malformed Sales URL ID is absent, not unavailable; a canonical but unresolved ID is unavailable until Retry or Clear.
