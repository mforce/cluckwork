# A farm code changes only through `rename-account` (#732)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted
**Date:** 2026-09-09

This decision **reverses epic #530 decision 10**, which deferred the rename and
recorded the slug as immutable for that epic. What #530 anticipated instead —
"renameable with a retired-code list" — is deliberately *not* what shipped; see
**What this does NOT cover**.

## What happened

Two halves, and only one of them is an incident.

**Earned.** A database provisioned before multi-farm tenancy and then upgraded
gets the code `default-farm` from migration `20260818235944_AddAccountSlug`.
Nothing asks for a code at migration time and no verb changed one afterwards, so
#731 documented the only path there was: a hand-guarded `UPDATE "Accounts"` run
through `psql`. That procedure shipped, and it was wrong in four ways at once.
It bumped `Version` by hand, so an off-by-one left the EF concurrency token
disagreeing with reality. It checked neither the slug pattern nor the reserved
set, so it could store an uppercase or reserved code that nobody could sign in
with. It wrote **no audit row**, and told the operator to record the change in
the deployment repo's change log instead — accountability by convention. And it
required a `postgres:` image reference in a tracked runbook to run the client at
all. Nothing caught any of this because nothing tests a document.

**No incident** for the retired-code half. Nobody has reused a retired farm code
in anger; the reuse behaviour below is a forward-looking choice, and a reader is
entitled to weigh it as one.

## The rule

A farm code changes **only** through `Account.Rename` reached by the
`rename-account` one-shot verb — never a raw `UPDATE`, never a second code path.
`Account.Rename` validates through the same `TryValidateSlug` provisioning uses,
returns `Result` rather than throwing, bumps `Version` itself on a real change
and deliberately does **not** bump it on a no-op. The service resolves the code
to an id, resolves the tenant, takes the tenant-keyed `FOR UPDATE`, and then
**re-compares the locked row's slug against the code the operator named**: the
lookup and the lock are two statements, so a rename that committed in between
would otherwise be silently overwritten. The unique index `IX_Accounts_Slug` is
the authority on the destination code, and the `DbUpdateException` catch is the
guarantee; the pre-read in front of it is convenience only. Break any of these
and a rename either loses a concurrent write, ships an unusable code, or leaves
no trace of who changed a farm's identity.

## Why not the obvious alternative

An HTTP endpoint or a Farm Settings field is what a reader reaches for first,
and both are refused. Epic #530 decision 2 already rejected a cross-tenant
operator HTTP API, and nothing about a rename argues for reopening it. A
Settings field is worse than merely inconsistent: the farm code is the first
field on the sign-in form, so a farm given a self-service rename can change the
credential its own staff are mid-shift typing, with no operator in the loop and
no way for a signed-out user to discover the new value. Keeping the only surface
an offline verb makes shell access the authorization boundary, which is the same
boundary `suspend-account`, `reactivate-account` and `recover-admin` already
use.

## What this does NOT cover

**There is no retired-code list.** A code a farm has moved off is immediately
reusable by any other farm, so `rename-account --slug <retired>` targets
whoever holds that code *now* — which may not be the farm the operator meant.
This is an accepted cost, not an oversight: the verb's header comment and the
operator runbook both instruct running `list-accounts` immediately before a
rename, and `AccountRenameServiceTests` pins the reuse behaviour so it is a
recorded fact rather than a surprise. #530's own sketch of this work assumed a
retired-code list; shipping without one is the deliberate difference.

It also does not cover the client-side caches, and they do not clear themselves.
An explicit sign-in with the new code prepends it to that device's remembered
farm-code roster and refreshes the per-farm palette cache under the new key. The
OLD remembered code is not removed: `removeFarmCode` (#587, the Forget control)
is the roster's only exit, so the sign-in form keeps offering the old code, and
offering it returns `Auth.UnknownFarmCode` unless another farm has since reused
it. Both caches are cosmetic. Sessions themselves are unaffected — cookies and
tokens bind to the account id, never to the code — and that is asserted, not
assumed.

Nothing here bounds what happens *outside* the deployment: printed material and
bookmarked `?farm=<old>` links are stale the moment the rename commits, and the
only mitigation is telling users the new code before it lands.

## How it is enforced

- `AccountRenameServiceTests` (`tests/Cluckwork.Api.IntegrationTests/`) — the
  stale-source fence, the no-op, not-found, taken-code and the two-fence race
  in which exactly one of two farms wins the destination code through the index
  catch. Both race tests hold real row locks rather than sleeping.
- `AccountSlugRaceTests.TwoConcurrentRenames_TheLoserGetsAConcurrencyConflict` —
  the `Version` token under two tracked snapshots, per the repo rule that every
  new aggregate mutation carries a parallel-race test.
- `AccountLifecycleCommandTests` — the verb's exit codes, printed lines,
  case-folding asymmetry, audit row and session survival, driven as a real
  subprocess.
- `CliDispatcherTests.Registry_ContainsEveryVerb_WithNoDuplicateNames` and
  `OneShotVerbMinimalConfigTests.EveryDispatchedVerb_HasAMinimalConfigCase` —
  registration and #347 process-role classification.
- `AuditVocabularyCoverageTests` — that `AuditActions.AccountRename` is a
  registry reference at the call site and is offered by the SPA.
- `TenantBypassRealTreeTests.RealSourceTree_AllBypassesAreAllowListed` — the
  service's one combined `IgnoreQueryFilters()` read, discovered
  once directly and once through its forwarding caller; both occurrences carry
  justifications.

Nothing enforces the runbook prose or the glossary wording; those rely on
review.
