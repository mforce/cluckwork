# #727 — Per-farm discount ceiling, with Owner/Manager approval above it

Epic #719, slice 6. Cut from `origin/main` at `e6b37d0`, after #720 (`cffed5e`),
#722 (`97c866f`), #723/#724 (`1a07441`), #721 (`c7864be`) and #758 (`e6b37d0`) shipped.

Synthesized from two independent design candidates (Phase B of `architect`). The
synthesis decision is in §7.

## 0. What already shipped, read from the code at `e6b37d0`

| Fact | Where |
|---|---|
| `ConfirmSaleHandler` ordered locking: Account `FOR SHARE` → SalesOrder `FOR UPDATE` → `CheckCanConfirm` → **fresh role re-read** → EggLots `FOR UPDATE` | `ConfirmSaleHandler.cs:90`, `:119`, `:134`, `:144`, `:169` |
| The fresh in-transaction role read is the authority; the JWT/route role is deliberately treated as stale (#612) | `ConfirmSaleHandler.cs:28-35`, `:144` |
| `account` at `:90` is the same locked row the farm timezone and `WorkerSaleAllocationPolicy` are read from | `ConfirmSaleHandler.cs:100-114`, `:174` |
| `CheckCanConfirm(code, note)` is pure order state + reason vocabulary; its check ORDER is pinned test-by-test | `SalesOrder.cs:148-150`, `:151-195` |
| `Confirm(code, note)` re-runs `CheckCanConfirm` as a guard, then bumps `Version` | `SalesOrder.cs:218`, `:225` |
| `HasBelowListLine` is a strict `<` against a non-null list | `SalesOrder.cs:211-212` |
| `ListUnitPriceMinorUnits: long?` and `ListPriceBasis` are paired by a throw in `Create`, set once, never re-resolved | `SalesOrder.cs:388-401` |
| `IEggGradeRepository` is **already injected** into `ConfirmSaleHandler` | `ConfirmSaleHandler.cs:19` |
| `GenericInsufficientStock()` exists so a restricted Worker never sees grade/quantity detail (#612 privacy) | `ConfirmSaleHandler.cs:37-44` |
| `EggLot.AssignedFlocksInsufficientStock` is a **role-conditional refusal on this same path, and it is a 422** | `ConfirmSaleHandler.cs:196-201` |
| `MapFailure` order: `.NotFound`/`Tenant.Mismatch` → 404, `Auth.Forbidden` → 403, `SalesOrder.NotDraft` → 409, **everything else → 422** | `SaleEndpoints.cs:320-341` |
| `Account.UpdateSettings` is a whole-block positional replace; `Version++` is unconditional | `Account.cs:178-259` |
| The settings handler escalates to `FOR UPDATE` only for currency/policy changes, both read-then-decide | `UpdateFarmSettingsHandler.cs:41-100` |
| `GET /account` is open to every role and already carries a per-caller DERIVED signal, never the raw policy | `AccountEndpoints.cs:37-52`, `:187-190` |
| `Account` mapped-property partition is asserted EXACTLY, counts hard-coded `10` / `8` | `BaseReferenceDataMigrationTests.cs:71-101` |
| `parseError()` does `i18n.t("errors:"+title, {defaultValue: detail})` and passes **no** interpolation values | `web/src/api/client.ts:70-114` |
| `lineDiscount(item)` already computes per-line discount in TS, formatted with the farm's separator | `web/src/routes/SalesPage.tsx:132-144`, `:218` |
| `confirm:${id}` is NOT a declared `dialogScopes` entry, so a confirm-POST failure lands in the page-level banner | `SalesPage.tsx:807-874`, `:1521` |
| The `ListPriceBasis` backfill relabels **every pre-2026-09-09 line** `PreDating`, then DROPs the default | `20260909143501_AddSalesOrderItemListPriceBasis.cs` |

### The decisive rows

**`CheckCanConfirm` holds rules about the ORDER; the ceiling is a rule about the ACTOR.**
That one sentence decides the shape. `CheckCanConfirm` runs *before* the role is known
and is re-run by `Confirm()` at mutation time; a rule whose answer depends on who is
asking cannot live there without making the aggregate refuse a legal transition for an
Owner. So the ceiling gets its own pure method and the handler calls it after the role read.

**The ceiling refusal has a role-conditional 422 precedent inside this very handler.**
`EggLot.AssignedFlocksInsufficientStock` is refused for a restricted Worker and allowed
for everyone else, and it is `Error.Domain` → 422, not `Auth.Forbidden` → 403. 403 here
means "your role cannot confirm sales orders at all"; Sales generically can.

## 1. Fixed decisions (owner's calls, made before implementation)

1. **Approval tier: Owner + Manager.** Sales and Worker are bound by the ceiling.
2. **Measured per LINE**, never against the order total. An order total is gameable by
   padding with at-list lines.
3. **Honest refusal, out of band.** The order stays `Draft`. No queue, no request object,
   no new order state, no notification.
4. **The ceiling VALUE is edited under the existing `AdminOnly` gate** (Owner + Manager),
   not Owner-only. `UpdateFarmSettingsCommand` is a whole-block positional replace, so a
   per-field Owner-only rule would be a new mixed-permission pattern on a screen with none.
   The issue body said "Owner-only"; its own correction comment withdrew that, and the
   title ("Owner/Manager approval") is the right one.
5. **A `PreDating` line is REFUSED, not waved through.** See §3.

## 2. Simplicity ceiling

Smallest viable implementation, in one sentence: one nullable `int` column on `Accounts`
holding basis points, one value type that owns the arithmetic, one pure aggregate query,
four lines in `ConfirmSaleHandler` between the role read and the stock lock, one derived
field on `GET /account`, and one number input on Farm Settings.

No new service, no new repository, no new port, no new endpoint, and **no edit to
`SaleEndpoints.cs`** — the new code falls into the existing `else → 422` branch.

## 3. `ListPriceBasis`: the fork the issue and the code disagreed on

`SalesOrder.cs:304-308` names #727 by number and says:

> for `ProductUnpriced` and `NotComparable`, "no comparable list price" is a RECORDED
> FACT and no discount is computable; for `PreDating` it means "we do not know", and the
> line may have been deeply discounted. **Those two need opposite treatment.**

#727's fifth acceptance criterion says the opposite: "A line with a `NULL` list price
cannot trip the ceiling." That criterion was written 2026-09-08; #720 shipped 2026-09-09
and refined the single NULL into a four-value basis. **The code wins**, and the issue's
criterion is amended in the same PR.

| Basis | At the ceiling check | Why |
|---|---|---|
| `Recorded` | Measured | The list price is a captured fact. |
| `ProductUnpriced` | Never violates | Recorded fact: the product had no list price, so nothing was discounted from anything. |
| `NotComparable` | Never violates | Recorded fact: currency or minor unit did not match, so no comparison exists. |
| `PreDating` | **Refused** for a ceiling-bound actor | We do not know. Fails closed. |

**Why `PreDating` must fail closed.** A pre-#720 draft can still be re-priced to anything
today. It carries no list price, so it does not trip `HasBelowListLine`, so #721 never
asks for a discount reason — and if the ceiling also skipped it, a ceiling-bound user
could give an unlimited discount with nothing recorded anywhere. That is the exact hole
this slice exists to close.

**Why the cost is bounded.** The population is every line older than 2026-09-09, it can
never grow (the migration DROPs the default and nothing in the application writes
`PreDating`), and it shrinks as those drafts are confirmed or voided. An Owner or Manager
confirms such an order untouched. Confirmed pre-#720 orders are unaffected — they are not
`Draft`, so they cannot be re-confirmed at all.

**`ProductUnpriced`/`NotComparable` are not a hiding place.** Minting or unpricing a
product is `AuthPolicies.AdminOnly` — exactly the Owner/Manager tier that may exceed the
ceiling anyway, so there is no privilege to gain. `NotComparable` additionally needs a
currency mismatch the currency lock makes unreachable on a live farm.

## 4. Shape

### 4.1 `DiscountCeiling` — the arithmetic, in one value type

`src/Cluckwork.Domain/Sales/DiscountCeiling.cs`. A `readonly record struct` over **basis
points** (0–10 000).

Basis points, not a percent, for two reasons. The comparison becomes exact integer
arithmetic with no division, so there is no rounding step that can disagree with itself at
the boundary. And a farm that later wants 12.5% needs no second migration. Basis points
are a *storage* choice: percent is what crosses the wire and what the screen shows.

The comparison, cross-multiplied so nothing is ever divided:

```
        discount / list  >  bp / 10 000
    (list − unit) * 10 000  >  bp * list
```

Three consequences, stated so nobody re-derives them:

- **Exactly on the boundary is ALLOWED.** "Maximum 10%" means at most 10%, so 10.00% off
  passes and 10.01% off does not. The strict `>` is the single place that lives.
- **A zero list price can never breach** — the right side is 0 and the left side is
  `−unit * 10 000 <= 0`. No divide-by-zero guard, no special case.
- **Both products are taken in `Int128`**, so no farm's minor units can overflow the
  multiply. One cast and the question stops existing.

`NULL` is the absence of a ceiling. **`0` is a legal, different setting** meaning "sales
staff may give nothing away". Collapsing the two is #719's own null-means-two-things trap.

### 4.2 `Account` — one nullable column

`int? MaxDiscountBasisPoints`, plain nullable `AddColumn<int>`, **no `defaultValue` and no
backfill**. Unlike `WorkerSaleAllocationPolicy` — where "no policy" was never a legal state,
so the column is `NOT NULL` with a default — "no ceiling" *is* the legal default here, so
NULL says it directly. Every existing farm is unaffected until someone types a number, and
`Account.Create()` needs no change.

`Account.UpdateSettings` gains the parameter second-to-last, immediately before the trailing
`bool financialRowsExist`, exactly where #612 put its own. `Version++` stays unconditional
across the whole block.

### 4.3 `SalesOrder` — one pure query, and `CheckCanConfirm` is not touched

```csharp
// #727. Pure, no mutation, no Version bump, no role and no ceiling LOOKUP —
// the caller decides WHO is bound; this decides WHETHER the order is over.
//
// Deliberately OUTSIDE CheckCanConfirm and deliberately NOT re-run by
// Confirm(): that method's check order is pinned test by test, it runs before
// the role is known, and Confirm() re-runs it at mutation time — so folding an
// ACTOR rule in would make the aggregate refuse a transition that is legal for
// an Owner.
public Result CheckWithinCeiling(DiscountCeiling ceiling);
```

It reports the **worst** offender, not the first in item order, so the message names the
most egregious line rather than whichever happens to sort first. An `Unmeasurable`
(`PreDating`) line ranks above every measurable breach — a discount that is unknown cannot
be compared against one that is known.

Two failure codes, because they say different things to the seller:

- `SalesOrder.DiscountCeilingExceeded` — a measured line is over the ceiling.
- `SalesOrder.DiscountNotMeasurable` — a `PreDating` line cannot be measured at all.
  Telling the seller "22.2%, above your 10% limit" here would be a fabricated number.

`SalesOrderItem` gains an `internal` `AgainstCeiling(ceiling)` returning
`LineCeilingStatus { Within, Exceeds, Unmeasurable }`, because the basis routing belongs to
the entity that owns the basis.

### 4.4 `ConfirmSaleHandler` — four lines, no new query, no new lock

Inserted between the existing step 5 (fresh role re-read) and step 6 (the Worker
assignment read), so it fails before any stock is touched, matching `CheckCanConfirm`'s own
rule:

```csharp
// 5b — the ceiling (#727). The ORDER's own rules were checked at step 4; this
// one is about the ACTOR, so it needs the role read above and belongs here.
if (account.MaxDiscount is { } ceiling && !MayExceedDiscountCeiling.Contains(role.Value))
{
    var within = order.CheckWithinCeiling(ceiling);
    if (within.IsFailure) { failure = Result.Failure<ConfirmSaleResponse>(within.Error); return false; }
}
```

`account` is the row already locked `FOR SHARE` at step 1. `role` is the fresh
in-transaction read #612 requires — a ceiling checked against a JWT claim, or against a
ceiling read outside the lock, would reopen that race.

`MayExceedDiscountCeiling = [Owner, Manager]`, a static beside the existing
`AllowedToConfirm` because it is the same kind of fact read from the same fresh role.

### 4.5 The refusal message — three audiences, no new plumbing

`Error` is a bare `record(Code, Description)` with no extension-data channel, and
`parseError()` passes no interpolation values. So the numbers cannot ride in on a
translated string. Three surfaces, each told what it can render:

1. **Server `detail`, English, with the real numbers and the offending line named by egg
   grade.** `IEggGradeRepository` is already injected and already used this way for the
   sibling insufficient-stock message, so naming the line costs no new dependency. This is
   what `curl`, k6, a seeder, the logs and every integration test read, and it is what
   satisfies the acceptance criterion for non-SPA callers.
2. **`errors:SalesOrder.DiscountCeilingExceeded`, parameter-free, in en/es/tl**, modelled
   on the shipped `errors["EggLot.AssignedFlocksInsufficientStock"]` — the existing
   precedent for "a refusal that tells you who can change this". No `{{placeholders}}`, so
   `catalogParity` is satisfied trivially and `parseError` is untouched.
3. **The SPA renders the numbers and marks the row**, because it already computes per-line
   discount percent and already formats with the farm's decimal separator.

A server-composed sentence is not merely awkward here, it is **wrong**: it would print
`18.0%` to a farm whose locale writes `18,0`. The numbers belong on the client by necessity.

### 4.6 `GET /account` — a derived, per-caller field

```csharp
/// #727 — the ceiling THIS caller is bound by, or null. Null covers both "the
/// farm sets none" and "you may exceed it", because both mean the same thing to
/// the screen: show no ceiling warning.
decimal? YourMaxDiscountPercent
```

Same standing as `ShowFarmWideSaleAllocationNotice`: a display hint derived server-side per
caller, never the authority. `GetAccount` already loads the full `Account` row for every
role, so this costs no new query.

Withholding it buys nothing — the refusal discloses the same number to the same person on
their first attempt — and costs exactly the blind retries the issue exists to prevent, plus
a discount-reason dialog answered for an order that was about to be refused.

**Stated contract:** the role here is claims-derived, so a user promoted mid-session sees a
stale hint until their token refreshes. The failure mode is a stale warning, never a wrong
outcome, because `ConfirmSaleHandler`'s fresh read is the authority.

### 4.7 The SPA — refuse before the POST, no new dialog plumbing

With the ceiling in hand, `SalesPage` marks the breaching row live while the price is being
typed (an **Over maximum** badge beside the existing below-list badge), shows a persistent
notice in the same shape `showFarmWideSaleAllocationNotice` already uses, and disables
Confirm. No POST is sent and no discount-reason dialog opens to collect a reason that is
about to be refused.

`confirm:${id}` stays out of `dialogScopes`. That is not a gap to plumb around: `askChoice()`
has already resolved and closed by the time the POST runs, so there is no dialog left to
render inside. The page-level banner below the order panel is the correct home for a
post-dialog-close failure, and with the pre-check it becomes the rare stale-client path
rather than the primary one.

### 4.8 Farm Settings — whole percents in the UI, basis points in storage

A `min=0 max=100 step=1` number input beside the `workerSaleAllocationPolicy` select, with
the label/hint key pair that field established. Blank clears the ceiling.

Whole percents for the first cut, deliberately: there is **no numeric field on this screen
today**, and `type="number"` handles `,` versus `.` inconsistently across browsers on a
screen that formats every other number by locale. Storage is basis points regardless, so
allowing 12.5% later is a UI change with no migration.

**Blank and `0` are different and both legal**, and the hint says so.

### 4.9 Settings write — the plain optimistic path

No `FOR UPDATE`. Argued from the race, not by analogy:

1. The locked path exists for **read-then-decide across aggregates**. Currency decides on
   "has this farm recorded any money?", read from other tables, then writes. The ceiling
   decides nothing from anywhere else; it range-checks its own input. It is structurally
   `Name`, `TimeZoneId` and `DateFormatOverride`, all of which stay optimistic today.
2. The read side is already serialized. `ConfirmSaleHandler` holds the Accounts row
   `FOR SHARE` from step 1 to commit, so a settings `UPDATE` either commits before that
   lock is taken or waits for it — no interleaving yields a torn read. This is the same
   guarantee `WorkerSaleAllocationPolicy`'s read at `:174` already relies on.
3. Two racing settings saves are already handled by `Version`.

What is given up: an order confirmed microseconds before the ceiling drops is judged under
the old ceiling. That is correct by definition — the rule in force at confirm time.

**The claim is falsifiable, and proving it is a deliverable.** An integration test copied
from `SaleAllocationPolicyTests.ConfirmSale_ParksOnTheAccountLock_AndReadsAPolicyChangeThatCommittedWhileItWaited`,
with the ceiling in place of the policy. If it goes red on the plain path, §4.9 was wrong.

## 5. Deliberately not done

- **No approval queue, request object, order state or notification** (fixed decision 3).
- **No ceiling on the order total**, no per-customer or per-product override.
- **`DiscountReasonCode.ManagerApproved` is left unenforced.** A reason describes the
  *sale*; the ceiling describes the *actor*. Coupling them would make #725's
  "discounts by reason" totals a function of who clicked confirm. Who actually approved is
  already recorded and recorded better — `AuditWriter` requires an actor (#500), so the
  `SalesOrder.Confirm` row already names them.
- **No `ceilingOverride` detail on the confirm audit payload.** It is the fact #728 needs
  and it belongs to #728, which also owns the alert it feeds.
- **No `listPriceBasis` on the sales-order read API.** The SPA's pre-check covers the
  measurable breach; the `PreDating` refusal arrives as a rare page-level 422 rather than
  teaching three more surfaces about the enum's wire form.

## 6. Guards, callers and obligations

- **`BaseReferenceDataMigrationTests.cs:71-101`** fails until `MaxDiscountBasisPoints` is
  placed in exactly one of the two sets and the literal `10` (or `8`) is bumped. Found by
  `grep -rn "AssertExactMappedPropertyPartition" tests/`, not by recall.
- **Positional-signature blast radius** (compiler-driven, so it cannot be missed silently):
  `CurrencyLockRaceTests.cs:65`, `CurrencyLockSerializationTests.cs:60`,
  `SalesProductTests.cs:326`, `AccountSettingsTests.cs`'s own `Update(...)` helper plus its
  four direct call sites, and `FarmSettingsTests.cs`'s `Body(...)` builder.
- **A dedicated migration test**, `WorkerSaleAllocationPolicyMigrationTests.cs` as the
  template: up, then down past it, then up again.
- **`docs/schema/` regenerated** by `tools/schema-docs/generate.sh` (#417, needs Docker).
  CI runs `--check` and fails a stale PR.
- **#394 non-CI callers, verified by READING:**
  `DemoDataSeeder.cs:363` confirms as the Owner, unaffected even if a ceiling existed.
  `SimulationDataSeeder.cs:1471` confirms as a per-order `SimActor` that may be a Sales
  persona, so it rests on the null ceiling plus the existing rule at `:1263-1266` that
  seeded orders never carry a below-list line. `tools/simulation/k6/` does not call confirm.
  **`tools/simulation/ui/specs/worker-sale-allocation.spec.ts:82-115` is the live hazard**:
  it drives a 0.01 price against a real list price as a Worker and expects either a 200 or
  the insufficient-stock 422. It survives unedited for exactly one reason — the fixture
  sets no ceiling. That is a load-bearing fact about a spec outside CI, so it gets an
  assertion that the seeded account's ceiling is null, and one line in the spec's header
  naming the dependency.
- **i18n**: every new key in en/es/tl or `catalogParity` fails the build. Help prose naming
  a control uses that control's label **per locale** (#688) — review-only.
- **GLOSSARY + Help**: `specs/product/GLOSSARY.md` gains *Discount ceiling*; the SPA
  glossary gains the matching entry with a `spec:` string that literally exists as a
  `**Term (#nnn)**` heading.
- **Screenshots** (#662): 1:1 before/after of Farm Settings and the Sales screen, from a
  stack rebuilt at the head under review, attached with `gh pr comment --attach`.
- **#727's fifth acceptance criterion is amended on the issue** to match §3.

## 7. Synthesis decision

**Base: candidate A.** It read `ListPriceBasis`'s comment correctly, it got the
locale-correctness argument for where the numbers render, and `DiscountCeiling` as a value
type hides more behind a smaller surface than a loose `int?` plus a static helper.

**Grafted from candidate B:** naming the offending line by **egg grade** in the English
`detail` (verified: `IEggGradeRepository` is already injected at `ConfirmSaleHandler.cs:19`,
so it costs no new dependency — candidate A's message named no line at all for non-SPA
callers); the observation that **`SaleEndpoints.cs` needs no edit**; and the cleaner framing
of why the column is plain-nullable with no backfill while `WorkerSaleAllocationPolicy`
needed one.

**Rejected from candidate B:** treating `PreDating` as never-violating. Its quotation of
`SalesOrder.cs:305` inserted a "[for display]" qualifier that is not in the source and
dropped the clause "and the line may have been deeply discounted" — the comment says the
opposite of what the candidate concluded, verified by reading the file. Also rejected: the
English-only refusal with no catalog key. The repo has three locales and a standing i18n
obligation, and this is a refusal a Sales user meets routinely, not an edge case.

**Rejected from candidate A:** the `ceilingOverride` confirm-audit payload, as #728 scope.

**Both candidates independently reached** the pure-method-outside-`CheckCanConfirm` shape,
422 over 403, basis points with cross-multiplied comparison and a strict `>`, the plain
optimistic settings path, and leaving `ManagerApproved` alone. Agreement across two models
on five forks is the strongest signal in this document.

## 8. Next implementation step

`DiscountCeiling` with `TryParsePercent` and `IsExceededBy`, plus its unit-test table —
boundary-exact allowed, one minor unit over refused, zero list price, zero ceiling, 100%
ceiling — because that table is the shared vector set the TypeScript mirror is later held to.

## 9. What the design got wrong, found while implementing it

Recorded here rather than by editing §§1–8, so the document still reads as the
design that was decided and this section reads as what surviving contact with the
code cost it. Five corrections, four of them defects in the design's own reasoning.

**§4.1's `Int128` rule names the wrong operation.** It says "both products are
taken in `Int128`" and names only the products. The **subtraction** is the actual
hazard: `(Int128)(list - unit)` subtracts in 64 bits and widens an already-wrapped
result, so `long.MaxValue - long.MinValue` becomes `-1` and the largest expressible
discount reports as no discount at all. The shipped form casts before subtracting,
`((Int128)list - unit) * 10_000`, and a test pins it.

**§4.1's zero-list-price claim holds only for non-negative unit prices.** "A zero
list price can never breach — no divide-by-zero guard, no special case" is true of
the comparison, which never divides, but not of the percent the refusal message
needs. `Money` is signed and `AddItem` applies no sign check, so a negative unit
price against a zero list reaches a division by zero. `AgainstCeiling`'s `Recorded`
arm carries a `list > 0` guard for that reason.

**§4.3 and §4.5 contradict each other.** §4.3 gives `CheckWithinCeiling(ceiling)` a
bare `Result` return and §4.4 shows the handler forwarding `within.Error` unchanged,
while §4.5 requires the English `detail` to name the offending line by egg grade —
a name the Domain assembly cannot resolve, since `SalesOrderItem` carries only an
`EggGradeId`. An `Error` whose string is already finished leaves the handler nothing
to enrich, and resolving grade names eagerly would add a query to the happy path.
Shipped instead as `FindCeilingBreach` returning a `CeilingBreach?`: the domain
decides **who** breaches, the handler composes the refusal. That is the seam the
same handler already uses twenty lines away, where `SaleAllocationPlan` returns
`ShortEggGradeId` and `ConfirmSaleHandler` builds `EggLot.InsufficientStock`. The
contradiction reads as a graft seam — grade-naming came from candidate B onto
candidate A's base (§7) and the two halves were never reconciled.

**§4.6 assumes a role is in scope on `GET /account`, and none was.** The endpoint
took `IAccountRepository`, `IFarmLogoRepository`, `TenantContext` and `FlockScope`.
The sibling `ShowFarmWideSaleAllocationNotice` sidesteps the problem by keying on
`flockScope.IsUnrestricted`, which is a flock-scope proxy and not a role, so it
could not be copied. The role comes from `ICurrentUser.Roles` through
`Roles.ResolveEffective`, which is what makes §4.6's "claims-derived, so a promoted
user sees a stale hint" contract accurate as written.

**§3's defence of `ProductUnpriced` does not reach the actor who gains.** It argues
that "minting or unpricing a product is `AuthPolicies.AdminOnly` — exactly the
Owner/Manager tier that may exceed the ceiling anyway, so there is no privilege to
gain." That reasons about the admin performing the action. The standing exemption
accrues to every Sales user and Worker selling that product afterwards, no screen
says the product is exempt, and a product simply **created without a price** is a
commoner route there than an admin deliberately unpricing one. The routing is still
judged correct — a discount from nothing is undefined, not zero — but that is a
different claim from the one §3 makes, and the two should not be read as the same.

**One addition the design did not call for.** `Account.MaxDiscount` resolves the
column through `DiscountCeiling.FromBasisPoints`, which throws outside 0–10 000, and
that getter is read on the role-agnostic `GET /api/v1/account` every authenticated
page load hits — so one out-of-range row would 500 the whole farm, including the
Settings screen that would correct it. `AccountConfiguration` declares a
`CK_Accounts_MaxDiscountBasisPoints` check constraint with an `IS NULL` arm, so the
range fails closed in both layers per #673 and the getter's throw is unreachable
rather than merely unlikely. #732 records that raw `UPDATE`s against `Accounts` do
happen, which is what makes this worth a constraint rather than a comment.
