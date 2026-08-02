# #277 — session decisions and assumptions

Unattended run, 2026-08-02. The owner answered the Phase-0 question set up front;
everything after that is recorded here as an `ASSUMPTION` with a reversal note.

## Answered up front (owner, Phase 0)

| # | Question | Answer |
| --- | --- | --- |
| 1 | Push + open a PR unattended? | **Yes** — push the branch, open a **draft** PR, run the codex review loop. Merging stays the owner's. |
| 2 | Scope of this run | **Everything**: E2E smoke + canary-under-load + the four #310/#311 acceptance items. |
| 3 | Personas in the first PR | **All five** — Owner, Manager, Sales, Worker, ReadOnly. |
| 4 | One slice issue or several | **Split**: smoke / canary / CI wiring, one issue → one PR. |
| 5 | Where Playwright lives | **`tools/simulation/ui/`** — standalone project beside the k6 scripts, not inside `web/`. |
| 6 | CI | **`workflow_dispatch` only** — never automatic, runnable from the Actions tab. |
| 7 | Core Web Vitals sink | **Both** — Playwright's own report/trace as the raw artifact, plus a summary folded into the #243 findings doc. |
| 8 | May the suite run `reset.sh` | **Yes, freely** — the fixture is throwaway and deterministic; `reset.sh` already guards the compose project name before `down -v`. |

## Settled from the repo / a probe, not spent on a question

**Browser strategy — probed, one workable option, so not asked.**
Playwright `1.62.1` launches the **system chromium** at
`/run/current-system/sw/bin/chromium` (Chromium 150.0.7871.128) via
`launchOptions.executablePath`, with `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`.
Verified live against the running sim stack: navigated `http://127.0.0.1:8081/`,
HTTP 200, `document.title === "Cluckwork"`, the SPA root rendered.
Playwright's **bundled Firefox fails to launch** on this box, exactly the NixOS
dynamic-linking trap the brief warned about. So: chromium only, system binary.

**Cast roles → SPA roles, decoded from real JWTs, not read off the cast file's labels.**
`.sim-cast.json`'s `role` field is descriptive (its own `notes` say so). What the
SPA actually derives (`web/src/auth/claims.ts`) was confirmed by logging each
persona in against the live stack and decoding the token:

| cast `role` | JWT `role` claim | SPA `Role` | `isAdmin` |
| --- | --- | --- | --- |
| `Owner` | `Admin` | `Admin` | yes |
| `Manager` | `Manager` | `Manager` | yes |
| `Sales` | `Sales` | `Sales` | no |
| `Worker` | *(claim absent)* | `Worker` | no |
| `ReadOnly` | `ReadOnly` | `ReadOnly` | no |

Cast composition: 1 Owner, 1 Manager, 1 Sales, 3 Worker, 4 ReadOnly.

**Where the worker flock restriction actually bites — probed, and it is not where
#277 assumed.** `SimulationDataSeeder.RestrictOneWorkerAsync` assigns
`sim-worker-1` to exactly one of the two flocks. #277 describes the consequence as
"sees only assigned flocks (403/hidden on the rest)". Measured against the live
fixture, that is not what happens:

- **Reads are not scoped at all.** `GET /flocks`, `GET /flocks/{id}` and
  `GET /daily-entries?flockId=` all return `200` for the *unassigned* flock, and
  the flock list is identical to what an unrestricted worker sees.
- **Writes are scoped**, and not with a 403: `POST /daily-entries` against the
  unassigned flock answers **`422 FlockScope.NotAssigned`** ("You are not assigned
  to this flock — ask an owner or manager"), while the same request against the
  assigned flock answers `201`.

Owner's call: **assert the real guarantee (the write refusal) and file the read
scoping as its own question**, rather than writing an assertion to a premise the
app does not implement. The read behaviour is filed separately — see the session
report.

## Assumptions recorded during the run

<!-- ASSUMPTION: <what> — <why> — <how to reverse> -->

**ASSUMPTION: the probe above wrote one Draft daily entry into the fixture**
(flock `Sim House A`, `2026-04-01`) — establishing where the restriction bites
needed a real write, and a read could not have answered it. This shifts
`SimulationDataSeeder`'s exact-count validation, so a re-seed now reports
`Failed` rather than `AlreadySeeded`. *Reverse:* `bash tools/simulation/reset.sh`,
which the suite is authorised to run and which the write-flow specs require
anyway.
