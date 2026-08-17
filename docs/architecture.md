# Architecture — the two orders that matter

Two things in this system are **ordered**, easy to get wrong, and described in
prose that nobody can hold in their head: the request pipeline, and the egg
loop's state machine. Both are drawn here, from the code rather than from the
prose — every claim below was read out of the files named beside it.

Layering (`Api` → `Application`/`Infrastructure` → `Domain`, and `Domain`
depends on nothing) is in [the README](../README.md#architecture). The rules
these diagrams illustrate live in [`AGENTS.md`](../AGENTS.md); the reasoning
behind each lives in [`docs/decisions/`](decisions/).

## The request pipeline

Registration order in `src/Cluckwork.Api/Program.cs`. Only the load-bearing
steps are drawn — the full list runs to 28 entries, most of which are edge
concerns whose relative order does not carry a guarantee.

```mermaid
flowchart TD
    CLI{"one-shot verb?<br/><i>migrate · seed · recover-admin<br/>bootstrap-admin · healthcheck</i>"}
    CLI -->|yes| EXIT["run, then exit — the HTTP<br/>pipeline is never registered"]
    CLI -->|no| EDGE

    EDGE["forwarded headers · security headers · cache defaults<br/>HSTS <i>(not in Development)</i> · exception handler<br/>HTTPS redirect · static files · request logging"]
    EDGE --> LIMITS["rate limiter · per-endpoint body caps"]
    LIMITS --> AUTHN["UseAuthentication<br/><i>JWT → HttpContext.User</i>"]
    AUTHN --> TENANT["TenantResolutionMiddleware<br/><i>account_id claim → TenantContext</i>"]
    TENANT --> EPOCH["CredentialEpochMiddleware<br/><i>fresh DB read, every request</i>"]
    EPOCH --> MCP["MustChangePasswordMiddleware<br/><i>403s everything but change-password + logout</i>"]
    MCP --> AUTHZ["UseAuthorization"]
    AUTHZ --> IDEM["IdempotencyMiddleware"]
    IDEM --> ENDPOINT["endpoint · /health · SPA fallback"]
```

Three positions in that chain are decisions, not accidents:

| Placement | Why | Break it and |
|---|---|---|
| `CredentialEpochMiddleware` **after** tenant resolution | It reads the user's current epoch from the tenant's database | A revoked credential keeps working (#364) |
| `MustChangePasswordMiddleware` **before** `UseAuthorization` | The gate then applies uniformly, whatever policy tier an endpoint carries | An endpoint's own policy decides whether a forced reset is enforced (#283) |
| `IdempotencyMiddleware` **after** `UseAuthorization` | A replay returns a cached response *without invoking the endpoint* | A role-denied caller replaying someone else's key gets the cached response instead of a 403 |

The epoch check is a database round trip on **every authenticated request**, on
purpose — the round trip *is* the fail-closed guarantee. Do not cache it.

## The egg loop

Three aggregates and one background job. `DailyEntry` produces stock,
`SalesOrder` consumes it, and **`EggLot` is an aggregate root in its own right
that is written directly** — do not read the two state machines below as the
complete set of inventory writers:

| Writer | Path | Effect on a lot |
|---|---|---|
| `DailyEntry.Submit` | `SubmitDailyEntryHandler` | creates lots |
| `DailyEntry` adjust / void | `AdjustDailyEntryHandler`, `VoidDailyEntryHandler` | `EggLot.AdjustProduction` reconciles the lot down to what the corrected day says, with the already-sold amount as the floor |
| `SalesOrder.Confirm` / `Void` | `ConfirmSaleHandler`, `VoidSaleHandler` | `Allocate` / `Restore` |
| **Manual stock movement** | `RecordEggLotMovementHandler` (`/stock`) | `EggLot.AdjustAvailable` for a `Discard`, `InternalUse` or `Reconciliation` movement — no daily entry and no sale involved |

Anything touching lot concurrency or the movement ledger has to account for all
four, not just the two drawn below.

### Daily entry

```mermaid
stateDiagram-v2
    [*] --> Draft: Create
    Draft --> Draft: RecordProduction<br/>(grades ≤ sellable)
    Draft --> Submitted: Submit<br/>(grades must reconcile EXACTLY)
    Submitted --> Locked: lock sweep, 7 days<br/>(farm-local date)
    Submitted --> ManagerAdjusted: ManagerAdjust(reason)
    Locked --> ManagerAdjusted: ManagerAdjust(reason)
    ManagerAdjusted --> ManagerAdjusted: ManagerAdjust(reason)
    Submitted --> Voided: Void(reason)
    Locked --> Voided: Void(reason)
    ManagerAdjusted --> Voided: Void(reason)
    Voided --> [*]
```

Worth reading off the diagram, because a linear "Draft → Submitted → Locked →
Voided" sketch gets all three wrong: **`ManagerAdjusted` is re-enterable**,
**`Void` is reachable from three states**, and **a Draft cannot be voided at
all** — it never generated anything to reverse. States and guards live in
`src/Cluckwork.Domain/Eggs/DailyEntry.cs`; the sweep is
`Infrastructure/Jobs/DailyEntryLockSweep.cs` (`Submitted` entries strictly older
than 7 farm-local days).

`Submit` is the transition that creates stock — in one transaction with the
state change (`Application/Features/DailyEntries/SubmitDailyEntry/`):

```mermaid
flowchart LR
    SUBMIT["Submit"] --> LOTS["one EggLot per grade line<br/>+ the cracked / dirty condition lots"]
    LOTS --> MOVES["one EggInventoryMovement PER LOT<br/><i>type: Production</i>"]
    SUBMIT --> BIRDS["BirdMovement <i>type: Mortality</i><br/>only when mortality > 0"]
    SUBMIT --> AUDIT["audit event"]
```

### Sale

```mermaid
stateDiagram-v2
    [*] --> Draft: Create
    Draft --> Draft: AddItem · UpdateItem · RemoveItem
    Draft --> Cancelled: Cancel
    Draft --> Confirmed: Confirm<br/>(needs ≥ 1 item — ALLOCATES STOCK)
    Confirmed --> Voided: Void(reason)<br/>(RESTORES STOCK)
    Cancelled --> [*]
    Voided --> [*]
```

`Confirm` is the only transition that decrements stock, and it does the whole
allocation before the state changes — insufficient stock on any line aborts the
transaction, so a half-allocated confirmed order cannot exist. Lots are drawn
**FIFO by `ProductionDate`, then `Id`** as tiebreaker
(`Infrastructure/Repositories/EggLotRepository.cs`), locked `FOR UPDATE`, and
each draw writes a `SalesOrderAllocation` row. `Void` re-locks those same lots
**in the same order** — that shared ordering is what stops confirm and void
deadlocking against each other — restores each quantity, and marks the
allocation rows released rather than deleting them.

Two things the enums imply but the code does not do:

- **`Shipped` and `Invoiced` are declared and never set.** They exist for later
  phases. The simulation seeder asserts both counts stay zero.
- **`EggLot.RestrictedUntil` is enforced but never written.** `Allocate` refuses
  a restricted lot and the FIFO query filters them out, so the guarantee is
  real — but no production path sets the field yet, because medication tracking
  is a later phase. The mechanism is ready; the writer is not.
